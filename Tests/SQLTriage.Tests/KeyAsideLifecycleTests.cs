/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The discriminate-and-prove lifecycle for DPAPI key-file asides (HOUSE RULE, Adrian
    /// 2026-08-09), driven against REAL DPAPI and REAL files on disk. Nothing here is faked: the
    /// sealed population is produced by protecting under an entropy this machine will then refuse,
    /// the unmeasurable population by a file held open with <see cref="FileShare.None"/>, the failed
    /// move and the failed delete the same way.
    ///
    /// <para>Every assertion is on the OUTCOME OF A MEASUREMENT the code took, never on a filename:
    /// the whole point of the rule is that a name says who wrote a file and never what is in it.</para>
    /// </summary>
    public sealed class KeyAsideLifecycleTests : IDisposable
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KeyAsideLifecycleTests.v1");
        private static readonly byte[] ForeignEntropy = Encoding.UTF8.GetBytes("KeyAsideLifecycleTests.foreign");
        private const DataProtectionScope Scope = DataProtectionScope.LocalMachine;

        private readonly string _dir;
        private readonly string _keyPath;
        private readonly List<(bool IsError, string Message)> _log = new();

        public KeyAsideLifecycleTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sqlt-keyaside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _keyPath = Path.Combine(_dir, ".test-key");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* temp dir */ }
        }

        private void Sink(bool isError, Exception? error, string message) => _log.Add((isError, message));

        private static byte[] Material(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

        private void WriteWrapped(string path, byte[] material, byte[] entropy) =>
            File.WriteAllBytes(path, ProtectedData.Protect(material, entropy, Scope));

        private string PlantAside(string suffix, Action<string> write)
        {
            var path = _keyPath + ".corrupt-" + suffix;
            write(path);
            return path;
        }

        // ── The classifier, one population at a time ────────────────────────

        [Fact]
        public void An_aside_that_unwraps_here_is_measured_Unwrapped()
        {
            var aside = PlantAside("20260810000001", p => WriteWrapped(p, Material(0x11), Entropy));

            var found = KeyAsideLifecycle.Classify(aside, Entropy, Scope, replacementMaterial: null);

            found.Reading.Should().Be(KeyAsideReading.Unwrapped,
                "DPAPI returned material for these bytes, which is a measurement and not an inference");
            found.DuplicatesReplacement.Should().BeFalse(
                "no replacement material was supplied, so nothing was compared");
        }

        [Fact]
        public void An_aside_DPAPI_refuses_is_measured_Sealed_not_Unmeasured()
        {
            // Sealed for real: protected under an entropy the classifier will not present, so
            // ProtectedData.Unprotect throws CryptographicException on this very machine.
            var aside = PlantAside("20260810000002", p => WriteWrapped(p, Material(0x22), ForeignEntropy));

            var found = KeyAsideLifecycle.Classify(aside, Entropy, Scope, Material(0x22));

            found.Reading.Should().Be(KeyAsideReading.Sealed,
                "the bytes were read and DPAPI refused them, which is different from not reading them");
            found.DuplicatesReplacement.Should().BeFalse(
                "material that was never recovered cannot have been compared with anything");
        }

        [Fact]
        public void A_zero_byte_aside_is_measured_Empty()
        {
            var aside = PlantAside("20260810000003", p => File.WriteAllBytes(p, Array.Empty<byte>()));

            KeyAsideLifecycle.Classify(aside, Entropy, Scope, Material(0x33))
                .Reading.Should().Be(KeyAsideReading.Empty);
        }

        [Fact]
        public void An_aside_whose_bytes_cannot_be_read_is_Unmeasured_not_a_verdict()
        {
            var aside = PlantAside("20260810000004", p => WriteWrapped(p, Material(0x44), Entropy));

            // A real read failure: an exclusive handle, which is what an antivirus scan or a backup
            // agent produces on this box. The file plainly WOULD unwrap; the point is that nothing
            // measured it, and the classifier must not report a verdict it did not take.
            using (File.Open(aside, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                KeyAsideLifecycle.Classify(aside, Entropy, Scope, Material(0x44))
                    .Reading.Should().Be(KeyAsideReading.Unmeasured);
            }

            KeyAsideLifecycle.Classify(aside, Entropy, Scope, Material(0x44))
                .Reading.Should().Be(KeyAsideReading.Unwrapped,
                    "with the handle released the same file measures readable, which is what makes "
                    + "the Unmeasured reading above a fact about the reading and not about the file");
        }

        [Fact]
        public void Duplication_is_decided_by_comparing_recovered_material_not_by_comparing_files()
        {
            var material = Material(0x55);

            // Two DIFFERENT ciphertexts of the SAME material: DPAPI salts every blob, so the files
            // are not byte-equal. A file comparison would call these different; the material is what
            // authorises the delete, so the material is what is compared.
            var aside = PlantAside("20260810000005", p => WriteWrapped(p, material, Entropy));
            WriteWrapped(_keyPath, material, Entropy);
            File.ReadAllBytes(aside).Should().NotEqual(File.ReadAllBytes(_keyPath),
                "this test's premise: the two wrapped files differ byte for byte");

            KeyAsideLifecycle.Classify(aside, Entropy, Scope, material)
                .DuplicatesReplacement.Should().BeTrue();

            KeyAsideLifecycle.Classify(aside, Entropy, Scope, Material(0x56))
                .DuplicatesReplacement.Should().BeFalse("different material is not a duplicate");
        }

        // ── The proof ───────────────────────────────────────────────────────

        [Fact]
        public void A_replacement_read_back_off_disk_and_unwrapped_to_the_same_material_is_proven()
        {
            var material = Material(0x66);
            WriteWrapped(_keyPath, material, Entropy);

            var proof = KeyAsideLifecycle.ProveReplacement(_keyPath, material, Entropy, Scope);

            proof.Proven.Should().BeTrue();
            proof.Detail.Should().Contain("read back from disk");
        }

        [Fact]
        public void A_replacement_holding_other_material_is_not_proven_and_the_detail_says_so()
        {
            WriteWrapped(_keyPath, Material(0x77), Entropy);

            var proof = KeyAsideLifecycle.ProveReplacement(_keyPath, Material(0x78), Entropy, Scope);

            proof.Proven.Should().BeFalse(
                "the file on disk and the key the process is about to use are different keys");
            proof.Detail.Should().Contain("NOT the key now in use");
        }

        [Fact]
        public void A_replacement_that_is_not_on_disk_at_all_is_not_proven()
        {
            var proof = KeyAsideLifecycle.ProveReplacement(_keyPath, Material(0x79), Entropy, Scope);

            proof.Proven.Should().BeFalse();
            proof.Detail.Should().Contain("could not be read back");
        }

        [Fact]
        public void A_replacement_that_will_not_unwrap_is_not_proven()
        {
            WriteWrapped(_keyPath, Material(0x7A), ForeignEntropy);

            var proof = KeyAsideLifecycle.ProveReplacement(_keyPath, Material(0x7A), Entropy, Scope);

            proof.Proven.Should().BeFalse();
            proof.Detail.Should().Contain("would not unwrap");
        }

        // ── The cleanup, which the proof authorises and nothing else does ───

        [Fact]
        public void Nothing_is_deleted_when_the_replacement_was_not_proven_and_every_aside_is_named()
        {
            var empty = PlantAside("20260810000010", p => File.WriteAllBytes(p, Array.Empty<byte>()));
            var dup = PlantAside("20260810000011", p => WriteWrapped(p, Material(0x88), Entropy));

            KeyAsideLifecycle.ReconcileAsides(
                _keyPath, Material(0x88), Entropy, Scope,
                new ReplacementProof(false, "the replacement was never written"), Sink);

            File.Exists(empty).Should().BeTrue("an unproven replacement authorises no delete at all");
            File.Exists(dup).Should().BeTrue();

            _log.Should().OnlyContain(e => e.IsError, "a kept aside on an unproven replacement is an Error");
            _log.Select(e => e.Message).Should().Contain(m => m.Contains(empty))
                .And.Contain(m => m.Contains(dup), "refusal names the path it kept");
            _log.Should().Contain(e => e.Message.Contains("the replacement was never written"),
                "the refusal states the measurement it rests on rather than asserting one");
        }

        [Fact]
        public void A_proven_replacement_removes_only_the_two_provably_worthless_populations()
        {
            var replacement = Material(0x99);
            WriteWrapped(_keyPath, replacement, Entropy);

            var empty = PlantAside("20260810000020", p => File.WriteAllBytes(p, Array.Empty<byte>()));
            var duplicate = PlantAside("20260810000021", p => WriteWrapped(p, replacement, Entropy));
            var otherMaterial = PlantAside("20260810000022", p => WriteWrapped(p, Material(0x9A), Entropy));
            var sealedAside = PlantAside("20260810000023", p => WriteWrapped(p, Material(0x9B), ForeignEntropy));

            var proof = KeyAsideLifecycle.ProveReplacement(_keyPath, replacement, Entropy, Scope);
            proof.Proven.Should().BeTrue("this test's premise");

            KeyAsideLifecycle.ReconcileAsides(_keyPath, replacement, Entropy, Scope, proof, Sink);

            File.Exists(empty).Should().BeFalse("zero bytes held no key material at all");
            File.Exists(duplicate).Should().BeFalse(
                "it unwrapped to the material now in use, so it was a second copy and no way back");
            File.Exists(otherMaterial).Should().BeTrue(
                "it unwraps to material the replacement does not hold: the only route back to whatever "
                + "was encrypted under it");
            File.Exists(sealedAside).Should().BeTrue(
                "DPAPI refused it, so nothing is known about it and the refusal may be transient");

            _log.Should().Contain(e => e.Message.Contains("Deleted " + empty)
                                       && e.Message.Contains("zero bytes"));
            _log.Should().Contain(e => e.Message.Contains("Deleted " + duplicate)
                                       && e.Message.Contains("same bytes as the key now in use"));
            _log.Should().Contain(e => e.IsError && e.Message.Contains("Kept " + otherMaterial));
            _log.Should().Contain(e => !e.IsError && e.Message.Contains("Kept " + sealedAside));
        }

        [Fact]
        public void An_aside_the_delete_cannot_touch_is_reported_as_still_on_disk()
        {
            var replacement = Material(0xA1);
            WriteWrapped(_keyPath, replacement, Entropy);
            var empty = PlantAside("20260810000030", p => File.WriteAllBytes(p, Array.Empty<byte>()));

            var proof = KeyAsideLifecycle.ProveReplacement(_keyPath, replacement, Entropy, Scope);

            // A REAL failed delete, and the share mode is the whole construction: FileShare.Read
            // lets the CLASSIFIER read the bytes (so the file really is measured Empty and really
            // does reach the delete) while withholding DELETE access, so File.Delete throws. The
            // first draft of this test used FileShare.None and proved something else entirely - the
            // read failed, the aside classified Unmeasured, and the delete was never attempted.
            using (File.Open(empty, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                KeyAsideLifecycle.Classify(empty, Entropy, Scope, replacement)
                    .Reading.Should().Be(KeyAsideReading.Empty,
                        "this test's premise: the file is readable through the handle, so the delete "
                        + "is the step that fails and not the measurement");

                KeyAsideLifecycle.ReconcileAsides(_keyPath, replacement, Entropy, Scope, proof, Sink);
            }

            File.Exists(empty).Should().BeTrue("this test's premise: the delete could not run");
            _log.Should().Contain(e => e.IsError && e.Message.Contains("STILL ON DISK")
                                       && e.Message.Contains(empty),
                "a delete that failed must never be logged as a delete that happened");
        }

        [Fact]
        public void Reconcile_does_not_throw_when_the_folder_has_nothing_beside_the_key()
        {
            var act = () => KeyAsideLifecycle.ReconcileAsides(
                _keyPath, Material(0xA2), Entropy, Scope, new ReplacementProof(true, "proven"), Sink);

            act.Should().NotThrow();
            _log.Should().BeEmpty("a sweep that found nothing has nothing to report");
        }

        // ── Finding candidates (a name), which is not judging them ──────────

        [Fact]
        public void The_sweep_looks_only_at_this_key_files_own_asides()
        {
            var mine = PlantAside("20260810000040", p => File.WriteAllBytes(p, new byte[] { 1 }));
            var neighbour = Path.Combine(_dir, ".other-key.corrupt-20260810000041");
            File.WriteAllBytes(neighbour, new byte[] { 2 });
            var unrelated = Path.Combine(_dir, ".test-key.backup");
            File.WriteAllBytes(unrelated, new byte[] { 3 });

            var found = KeyAsideLifecycle.FindAsides(_keyPath);

            found.Should().ContainSingle().Which.Should().Be(mine);
            found.Should().NotContain(neighbour).And.NotContain(unrelated);
        }

        // ── The rename itself ───────────────────────────────────────────────

        [Fact]
        public void SetAside_moves_the_file_and_never_leaves_it_at_its_own_path()
        {
            WriteWrapped(_keyPath, Material(0xB1), ForeignEntropy);

            var aside = KeyAsideLifecycle.SetAside(_keyPath, Sink);

            aside.Preservation.Should().Be(KeyAsidePreservation.Moved);
            aside.Path.Should().NotBeNull();
            aside.SafeToOverwrite.Should().BeTrue("the material is preserved, so the write is safe");
            File.Exists(_keyPath).Should().BeFalse("a move, not a copy");
            File.Exists(aside.Path!).Should().BeTrue();
            _log.Should().ContainSingle().Which.Message.Should().Contain(aside.Path!);
        }

        [Fact]
        public void A_second_aside_never_overwrites_the_first()
        {
            WriteWrapped(_keyPath, Material(0xB2), Entropy);
            var first = KeyAsideLifecycle.SetAside(_keyPath, Sink);

            WriteWrapped(_keyPath, Material(0xB3), Entropy);
            var second = KeyAsideLifecycle.SetAside(_keyPath, Sink);

            first.Path.Should().NotBeNull();
            second.Path.Should().NotBeNull();
            second.Path.Should().NotBe(first.Path, "two asides in the same second take different names");
            File.Exists(first.Path!).Should().BeTrue();
            File.Exists(second.Path!).Should().BeTrue();

            // And they still hold what they held: the material, recovered, not the bytes.
            KeyAsideLifecycle.Classify(first.Path!, Entropy, Scope, Material(0xB2))
                .DuplicatesReplacement.Should().BeTrue();
            KeyAsideLifecycle.Classify(second.Path!, Entropy, Scope, Material(0xB3))
                .DuplicatesReplacement.Should().BeTrue();
        }

        [Fact]
        public void A_move_that_cannot_run_reports_Failed_and_says_the_replacement_is_refused()
        {
            WriteWrapped(_keyPath, Material(0xB4), Entropy);

            // A REAL failed move: an exclusive handle on the key file, which is the state every
            // caller of SetAside used to write a fresh key over.
            KeyAsideResult aside;
            using (File.Open(_keyPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                aside = KeyAsideLifecycle.SetAside(_keyPath, Sink);
            }

            aside.Preservation.Should().Be(KeyAsidePreservation.Failed, "nothing was preserved");
            aside.Path.Should().BeNull();
            aside.SafeToOverwrite.Should().BeFalse(
                "RULED 2026-08-10: a caller that could not preserve the material refuses to overwrite it");
            aside.Detail.Should().NotBeNullOrWhiteSpace(
                "the refusal quotes this, so it must carry the reason the move failed");
            aside.Obstruction.Should().Be(KeyAsideObstruction.SourceHeldOpen,
                "the handle is still open while SetAside runs, so the probe finds the file held and "
                + "this is the one reading under which a retry is real advice");

            _log.Should().ContainSingle().Which.IsError.Should().BeTrue();
            _log[0].Message.Should().Contain("No fresh key is being written over it",
                "the line has to state the consequence the ruling produces, not the one it replaced");
            _log[0].Message.Should().NotContain("being lost now",
                "that was the pre-ruling consequence and it is no longer what happens");
            _log[0].Message.Should().Contain(_keyPath);
        }

        [Fact]
        public void SetAside_on_a_path_with_no_file_is_NothingToPreserve_and_still_safe_to_overwrite()
        {
            var aside = KeyAsideLifecycle.SetAside(_keyPath, Sink);

            aside.Preservation.Should().Be(KeyAsidePreservation.NothingToPreserve);
            aside.SafeToOverwrite.Should().BeTrue(
                "no file means nothing is destroyed by the write, which is the opposite position "
                + "from a failed move; the nullable path used to collapse the two into one value");
            _log.Should().BeEmpty();
        }

        [Fact]
        public void A_move_blocked_at_its_DESTINATION_is_diagnosed_there_and_not_as_a_lock()
        {
            WriteWrapped(_keyPath, Material(0xB5), Entropy);
            var original = File.ReadAllBytes(_keyPath);

            // A directory at every name the next few seconds could produce. File.Exists is false for
            // a directory, so the collision loop does not advance past it and File.Move fails, with
            // NOTHING holding the key file open. This is the state the fixed advice was wrong about.
            var blockers = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                var target = _keyPath + ".corrupt-" + DateTime.UtcNow.AddSeconds(i).ToString("yyyyMMddHHmmss");
                Directory.CreateDirectory(target);
                blockers.Add(target);
            }

            var aside = KeyAsideLifecycle.SetAside(_keyPath, Sink);

            aside.Preservation.Should().Be(KeyAsidePreservation.Failed);
            aside.Obstruction.Should().Be(KeyAsideObstruction.DestinationOccupied,
                "the key file opened exclusively here, and the name the move needed is taken");
            aside.BlockedDestination.Should().NotBeNull("an operator cannot clear a name nobody stated");
            blockers.Should().Contain(aside.BlockedDestination!, "this test's premise");
            File.ReadAllBytes(_keyPath).Should().Equal(original,
                "the source file is untouched, which is what a destination-side failure looks like");
        }

        [Fact]
        public void A_path_with_no_file_carries_no_obstruction_at_all()
        {
            var nothing = KeyAsideLifecycle.SetAside(_keyPath + ".not-here", Sink);

            nothing.Preservation.Should().Be(KeyAsidePreservation.NothingToPreserve,
                "a path with no file never reaches the move");
            nothing.Obstruction.Should().Be(KeyAsideObstruction.NotApplicable,
                "nothing failed, so nothing was measured and nothing may be advised");
            nothing.BlockedDestination.Should().BeNull("no move was attempted, so no name was needed");
        }

        // ── The two refusal sentences (postures (b) and (c), RULED 2026-08-10) ──

        private static KeyAsideResult Failed(
            KeyAsideObstruction obstruction, string detail, string? destination = null) =>
            new(KeyAsidePreservation.Failed, null, detail, obstruction, destination);

        [Fact]
        public void The_unpreserved_refusal_names_the_file_the_stake_the_cause_and_a_way_forward()
        {
            var message = KeyAsideLifecycle.RefusalNotPreserved(
                _keyPath, "the thing at stake",
                Failed(KeyAsideObstruction.SourceHeldOpen, "IOException: the file is in use"));

            message.Should().Contain(_keyPath, "an operator cannot act on a file nobody named");
            message.Should().Contain("the thing at stake");
            message.Should().Contain("IOException: the file is in use", "the cause is quoted, not asserted");
            message.Should().Contain("No new key was written",
                "the disk state has to be stated, because the operator's next move depends on it");
            message.Should().Contain("start", "a refusal with no way forward is an outage");
            message.Should().NotContain("—").And.NotContain("…",
                "this reaches an operator; house rule, no em-dashes or ellipses in operator copy");
        }

        /// <summary>
        /// The remedy is chosen by the MEASUREMENT and by nothing else (RULED 2026-08-10, after the
        /// fixed paragraph told an operator to close a handle and restart in a state where nothing
        /// held the file and a restart cleared nothing). Each arm is asserted for what it must say
        /// AND for the wrong remedy it must not offer, because the defect was a true-sounding
        /// sentence beside a correct verdict.
        /// </summary>
        [Fact]
        public void A_destination_side_refusal_names_the_destination_and_does_not_offer_the_lock_remedy()
        {
            var blocked = _keyPath + ".corrupt-20260810120000";
            var message = KeyAsideLifecycle.RefusalNotPreserved(
                _keyPath, "the thing at stake",
                Failed(KeyAsideObstruction.DestinationOccupied,
                    "IOException: Cannot create a file when that file already exists.", blocked));

            message.Should().Contain(blocked,
                "the obstruction is at that name, and an operator cannot find a blocker nobody named");
            message.Should().NotContain("close whatever has",
                "nothing held the key file; three of the four remedies used to be for a lock");
            message.Should().NotContain("antivirus",
                "naming a scanner here is advice for a condition this run measured the absence of");
            message.Should().Contain("not a lock");
            message.Should().Contain("start SQLTriage again", "a refusal with no way forward is an outage");
        }

        [Fact]
        public void A_permission_refusal_says_a_restart_will_not_clear_it_and_names_the_folder()
        {
            var message = KeyAsideLifecycle.RefusalNotPreserved(
                _keyPath, "the thing at stake",
                Failed(KeyAsideObstruction.PermissionDenied, "UnauthorizedAccessException: Access to the path is denied."));

            message.Should().Contain(_dir, "the folder's permissions are the thing to check");
            message.Should().Contain("refused for permission");
            message.Should().NotContain("may be all this needs",
                "a permission refusal does not pass on its own, so the transient framing is a lie here");
            message.Should().NotContain("antivirus");
        }

        [Fact]
        public void An_undiagnosed_refusal_says_so_rather_than_borrowing_a_remedy()
        {
            var message = KeyAsideLifecycle.RefusalNotPreserved(
                _keyPath, "the thing at stake",
                Failed(KeyAsideObstruction.Undiagnosed, "IOException: something unforeseen"));

            message.Should().Contain("could not establish why",
                "the fail-safe reading states its own limit; that is the whole reason it exists");
            message.Should().NotContain("antivirus");
            message.Should().Contain("start SQLTriage again");
        }

        [Fact]
        public void Every_obstruction_reading_produces_its_own_remedy_and_no_two_share_one()
        {
            var remedies = Enum.GetValues<KeyAsideObstruction>()
                .Select(o => KeyAsideLifecycle.RefusalNotPreserved(
                    _keyPath, "the thing at stake", Failed(o, "detail", _keyPath + ".corrupt-x")))
                .ToList();

            remedies.Should().OnlyHaveUniqueItems(
                "two readings sharing a remedy is the defect this closed: advice that does not move "
                + "when the measurement does was never conditioned on it");
            remedies.Should().OnlyContain(m => !m.Contains("—") && !m.Contains("…"),
                "house rule, no em-dashes or ellipses in operator copy");
        }

        [Fact]
        public void The_unproven_refusal_says_the_new_file_exists_and_the_asides_are_intact()
        {
            var message = KeyAsideLifecycle.RefusalNotProven(
                _keyPath, "the thing at stake", "it would not unwrap");

            message.Should().Contain(_keyPath);
            message.Should().Contain("the thing at stake");
            message.Should().Contain("it would not unwrap");
            message.Should().Contain("wrote a new key file",
                "the disk is in a DIFFERENT state from the unpreserved refusal and must not borrow "
                + "its sentence: a file has been written by the time this one is composed");
            message.Should().Contain("still there",
                "the preserved files are the recovery route and the refusal has to point at them");
            message.Should().NotContain("—").And.NotContain("…");
        }
    }
}
