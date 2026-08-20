/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests.Licensing
{
    /// <summary>
    /// The three fail-closed key postures (RULED by Adrian, 2026-08-10) driven END TO END through the
    /// real <see cref="SeatRegister"/>: real files, real DPAPI, real failed moves and a real failed
    /// read-back. Nothing is stubbed and NO SEAM WAS ADDED to the service for any of it.
    ///
    /// <para>(a) Material that unwraps but is the wrong length is preserved before the key is
    /// regenerated. (b) A key file that could not be preserved is not overwritten; the service
    /// refuses. (c) A replacement key that cannot be proven readable back off disk does not become
    /// the key in force.</para>
    ///
    /// <para><b>How each condition is produced, and why it is genuine.</b>
    /// <list type="bullet">
    /// <item>Wrong-length material: DPAPI-wrapped under the service's OWN entropy, read out of the
    /// production field by reflection. Reflection reads a constant; it does not open a seam, and a
    /// rename of that field breaks these tests loudly rather than silently faking the plant.</item>
    /// <item>A failed move: a live handle on the key file opened <c>FileShare.Read</c>. Read access
    /// is still shared, so the service's own <c>File.ReadAllBytes</c> succeeds and the path reaches
    /// the move, which then fails because the handle shares no DELETE. <c>FileShare.None</c> would
    /// have failed the READ instead and never reached the branch under test.</item>
    /// <item>A failed read-back: the injected <see cref="ILogger{T}"/> is a production constructor
    /// parameter, and the service logs "Generated new seat-register HMAC key" between writing the
    /// file and proving it. Taking an exclusive handle inside that logger call is synchronous and
    /// deterministic, and it is exactly the real-world fault the posture exists for (a scanner or a
    /// backup agent holding a file open for a moment).</item>
    /// </list></para>
    ///
    /// <para><b>Coverage limit, stated rather than implied.</b> These prove the postures at ONE of
    /// the three key-holding services. <c>SqliteCipherHelper</c> and <c>CredentialProtector</c>
    /// resolve their key path from <c>AppContext.BaseDirectory</c>, which is process-global and read
    /// by the rest of the suite; driving them here rotated the host's cipher key and turned a green
    /// run red once already (see <c>AsideProducerCensusTests</c>). Their wiring is held by the lint
    /// in that file, which can establish the calls are present and cannot establish they run.</para>
    /// </summary>
    public sealed class SeatRegisterKeyPostureTests : IDisposable
    {
        private readonly string _tempDir;

        public SeatRegisterKeyPostureTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "seat-key-posture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_tempDir, recursive: true); } catch (Exception) { /* test cleanup */ }
        }

        // ── Plumbing ────────────────────────────────────────────────────────

        /// <summary>The service's own DPAPI entropy, read out of the production field.</summary>
        private static byte[] ServiceEntropy =>
            (byte[])typeof(SeatRegister)
                .GetField("HmacKeyEntropy", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;

        private static FakeBundleAccessor Bundle() =>
            new()
            {
                Features = new BundleFeatures(
                    RagEnabled: false, SpBlitzImport: true, FullCorpus: true,
                    PermittedCheckIds: Array.Empty<int>(),
                    InstanceSeats: null,
                    InstanceSwapsAllowed: 2),
            };

        private static InstanceFingerprint Instance(string machine) =>
            new()
            {
                MachineName = machine,
                InstanceName = null,
                ServerName = machine,
                MasterCreateDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };

        /// <summary>Key material this machine's DPAPI will hand back to the service, at any length.</summary>
        private static byte[] WrappedForTheService(int lengthBytes, byte fill) =>
            ProtectedData.Protect(
                Enumerable.Repeat(fill, lengthBytes).ToArray(),
                ServiceEntropy,
                DataProtectionScope.LocalMachine);

        /// <summary>Key material this machine's DPAPI will REFUSE the service: a foreign entropy.</summary>
        private static byte[] WrappedForNobody() =>
            ProtectedData.Protect(
                Enumerable.Repeat((byte)0x5A, 32).ToArray(),
                Encoding.UTF8.GetBytes("not-the-seat-register-entropy"),
                DataProtectionScope.LocalMachine);

        /// <summary>
        /// An <see cref="ILogger{T}"/> that takes an exclusive handle on a file the first time a
        /// chosen sentence is logged, and records every line. The service already takes a logger, so
        /// this adds nothing to it. Used to make the read-back in ProveReplacement genuinely fail.
        /// </summary>
        private sealed class HandleGrabbingLogger : ILogger<SeatRegister>, IDisposable
        {
            private readonly string _trigger;
            private readonly string _grabPath;
            private FileStream? _held;

            public HandleGrabbingLogger(string trigger, string grabPath)
            {
                _trigger = trigger;
                _grabPath = grabPath;
            }

            public List<string> Lines { get; } = new();

            /// <summary>The test's PREMISE. A test whose fault never armed proves nothing.</summary>
            public bool Grabbed { get; private set; }

            public void Release()
            {
                _held?.Dispose();
                _held = null;
            }

            public void Dispose() => Release();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var line = formatter(state, exception);
                Lines.Add(line);

                if (!Grabbed && line.Contains(_trigger, StringComparison.Ordinal))
                {
                    // Not swallowed: a grab that failed would leave the test asserting a refusal
                    // that never happened, and Grabbed is checked by every test that uses this.
                    _held = new FileStream(
                        _grabPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    Grabbed = true;
                }
            }
        }

        // ── (a) Wrong-length material that UNWRAPS ──────────────────────────

        [Fact]
        public void Wrong_length_material_that_unwraps_is_preserved_before_the_key_is_regenerated()
        {
            var keyPath = Path.Combine(_tempDir, "seats.key");
            var dbPath = Path.Combine(_tempDir, "seats.db");

            // 16 bytes, wrapped under the service's own entropy: DPAPI really does hand this back.
            // Before the ruling this branch logged a warning and regenerated straight over it.
            var readable = WrappedForTheService(lengthBytes: 16, fill: 0x3C);
            File.WriteAllBytes(keyPath, readable);

            var register = new SeatRegister(
                Bundle(), NullLogger<SeatRegister>.Instance, dbPath: dbPath, keyPath: keyPath);
            register.ClaimOnProbe(Instance("POSTUREA1"), "POSTUREA1").Allowed.Should().BeTrue();

            var asides = Directory.GetFiles(_tempDir, "seats.key.corrupt-*");
            asides.Should().ContainSingle(
                "material that unwraps is readable key material and is the only route back to what "
                + "was signed under it; the wrong length makes it unusable, not worthless");
            File.ReadAllBytes(asides[0]).Should().Equal(readable,
                "the aside holds the ORIGINAL bytes: it is a move, not a rewrite");

            // And what was preserved really is still readable here, which is the whole point.
            ProtectedData.Unprotect(File.ReadAllBytes(asides[0]), ServiceEntropy, DataProtectionScope.LocalMachine)
                .Should().HaveCount(16);

            // The live key was replaced with a usable one.
            File.Exists(keyPath).Should().BeTrue();
            ProtectedData.Unprotect(File.ReadAllBytes(keyPath), ServiceEntropy, DataProtectionScope.LocalMachine)
                .Should().HaveCount(32);
        }

        [Fact]
        public void Wrong_length_material_that_cannot_be_preserved_is_not_overwritten_either()
        {
            var keyPath = Path.Combine(_tempDir, "seats.key");
            var dbPath = Path.Combine(_tempDir, "seats.db");

            var readable = WrappedForTheService(lengthBytes: 16, fill: 0x3D);
            File.WriteAllBytes(keyPath, readable);
            var blockade = BlockEveryAsideName(keyPath);

            var register = new SeatRegister(
                Bundle(), NullLogger<SeatRegister>.Instance, dbPath: dbPath, keyPath: keyPath);
            var verdict = register.VerifyChain();
            blockade.AssertCovered(DateTime.UtcNow);

            verdict.Should().NotBeNull("the chain cannot be verified without a key");
            verdict!.Should().Contain("will not replace the key file",
                "posture (a) hands its aside to the same refusal as posture (b): readable material "
                + "that could not be preserved is not overwritten either");
            File.ReadAllBytes(keyPath).Should().Equal(readable,
                "the material is untouched, which is the entire point of refusing");
            Directory.GetFiles(_tempDir, "seats.key.corrupt-*").Should().BeEmpty();
        }

        // ── (b) A failed preservation means REFUSE ──────────────────────────

        /// <summary>
        /// The planted obstruction, and the instant its cover runs out.
        ///
        /// <para><see cref="AssertCovered"/> exists so a lost wall-clock race fails as itself. Without
        /// it the window expiring looks EXACTLY like the service having stopped refusing — the same
        /// assertions, the same messages — and that is what cost a CI run on 2026-08-15 before anyone
        /// could tell the two apart.</para>
        /// </summary>
        private sealed record AsideBlockade(List<string> Names, DateTime CoversThroughUtc)
        {
            /// <summary>
            /// Asserts the service call landed inside the planted range. Pass the clock read taken
            /// IMMEDIATELY after the call under test, not at the end of the test: the move happened
            /// at or before that instant, so it is the tightest honest upper bound.
            /// </summary>
            internal void AssertCovered(DateTime observedUtc) =>
                observedUtc.Should().BeBefore(CoversThroughUtc,
                    "this test's premise: the service stamps its aside name off the wall clock, so "
                    + "the whole call has to land inside the planted range. Past the range the names "
                    + "are free, the move SUCCEEDS, and every assertion below fails for a reason that "
                    + "has nothing to do with the posture. If THIS is what failed, widen the window "
                    + "in BlockEveryAsideName; the assertions themselves are not the problem");
        }

        /// <summary>
        /// Blocks every aside name the service could pick for the next <paramref name="seconds"/>
        /// seconds by planting a DIRECTORY at each one. <c>File.Exists</c> is false for a directory,
        /// so the collision loop never advances past it and <c>File.Move</c> fails.
        ///
        /// <para>This condition, not a file lock, is what makes the byte-identity assertion below
        /// load-bearing. MEASURED 2026-08-10: with a <c>FileShare.Read</c> handle the write that
        /// follows the move fails too, so the original survives even with the refusal removed, and
        /// only the refusal MESSAGE discriminates. Here the source file is not held at all, so a
        /// build without the refusal really does mint a key over it.</para>
        ///
        /// <para><b>Why the window is 120 seconds and not 5 (2026-08-15).</b> The service stamps its
        /// aside name from <c>DateTime.UtcNow</c> at the moment of the move, so the block only holds
        /// while that moment falls inside the planted range. That is a WALL-CLOCK race between this
        /// plant and the service call, and it is not the posture under test in any way. At 5 seconds
        /// it lost the race once on CI: one red run on 2026-08-15, a rerun of the IDENTICAL commit
        /// green, and no code change anywhere in the diff. MEASURED on this box the same day: these
        /// tests take 50-450 ms end to end, in isolation and inside the full 3645-test suite alike,
        /// so 5 s was already ~10x the cost and a shared runner still stalled past it. A margin that
        /// a stall can eat is not a margin; 120 s is ~250x and still plants in well under a second.
        /// Widening changes only how long the ARTIFICIAL obstruction lasts. Every assertion still
        /// fails if the service stops refusing, because the refusal is the thing they read.</para>
        ///
        /// <para>The base instant is captured ONCE, deliberately. Reading <c>DateTime.UtcNow</c> per
        /// iteration (as this did until 2026-08-15) lets a stall mid-loop skip a second and leave a
        /// HOLE inside the range — the same flake with a rarer trigger and a worse story.</para>
        /// </summary>
        private static AsideBlockade BlockEveryAsideName(string keyPath, int seconds = 120)
        {
            var baseUtc = DateTime.UtcNow;
            var planted = new List<string>();
            for (int i = 0; i < seconds; i++)
            {
                var stamp = baseUtc.AddSeconds(i).ToString("yyyyMMddHHmmss");
                var target = keyPath + ".corrupt-" + stamp;
                Directory.CreateDirectory(target);
                planted.Add(target);
            }

            // Exclusive end of the cover. The stamp format truncates to the second, so the planted
            // names are seconds [floor(baseUtc) .. floor(baseUtc) + seconds - 1], and the last of
            // those runs out at floor(baseUtc) + seconds.
            var coversThrough = new DateTime(
                    baseUtc.Year, baseUtc.Month, baseUtc.Day,
                    baseUtc.Hour, baseUtc.Minute, baseUtc.Second, DateTimeKind.Utc)
                .AddSeconds(seconds);

            return new AsideBlockade(planted, coversThrough);
        }

        [Fact]
        public void A_key_file_that_could_not_be_moved_aside_is_not_overwritten()
        {
            var keyPath = Path.Combine(_tempDir, "seats.key");
            var dbPath = Path.Combine(_tempDir, "seats.db");

            var unreadable = WrappedForNobody();
            File.WriteAllBytes(keyPath, unreadable);
            var blockade = BlockEveryAsideName(keyPath);

            var register = new SeatRegister(
                Bundle(), NullLogger<SeatRegister>.Instance, dbPath: dbPath, keyPath: keyPath);
            var verdict = register.VerifyChain();
            blockade.AssertCovered(DateTime.UtcNow);

            File.ReadAllBytes(keyPath).Should().Equal(unreadable,
                "RULED 2026-08-10: material that could not be preserved is not overwritten. Without "
                + "the refusal this file holds a freshly minted key and the original is gone, and "
                + "nothing here was holding the file open to stop that write");
            Directory.GetFiles(_tempDir, "seats.key.corrupt-*").Should().BeEmpty(
                "the move is the thing that failed, so there is no aside FILE");
            blockade.Names.Should().OnlyContain(b => Directory.Exists(b), "this test's premise");

            verdict.Should().NotBeNull();
            verdict!.Should().Contain("will not replace the key file");
            verdict.Should().Contain(keyPath, "an operator cannot act on a file nobody named");
            verdict.Should().Contain("could never be verified again",
                "the refusal states what was at stake, in this service's own terms");
            verdict.Should().Contain("start SQLTriage again", "a refusal with no way forward is an outage");

            // RULED 2026-08-10: the remedy is conditioned on the same measurement as the verdict.
            // This state is a blocked DESTINATION. Until the fix the message told the operator to
            // close whatever held the key file open and restart, in a state where nothing held it
            // and a restart cleared nothing, and it never named the thing actually in the way.
            blockade.Names.Should().Contain(b => verdict!.Contains(b, StringComparison.Ordinal),
                "the obstruction is at one of those names, so the message has to state which");
            verdict.Should().NotContain("close whatever has",
                "nothing here holds the key file open, and this run measured that before advising");
            verdict.Should().NotContain("antivirus",
                "a scanner is the remedy for the lock this run measured the absence of");
        }

        [Fact]
        public void The_preservation_refusal_is_per_attempt_and_clears_when_the_lock_does()
        {
            var keyPath = Path.Combine(_tempDir, "seats.key");
            var dbPath = Path.Combine(_tempDir, "seats.db");

            var unreadable = WrappedForNobody();
            File.WriteAllBytes(keyPath, unreadable);

            using (new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var refused = new SeatRegister(
                        Bundle(), NullLogger<SeatRegister>.Instance, dbPath: dbPath, keyPath: keyPath)
                    .VerifyChain();

                refused.Should().Contain("will not replace the key file");

                // The OTHER leg of the conditioned remedy (RULED 2026-08-10). Here the file really
                // is held open, this run measured that, and the transient framing is the honest one.
                // The destination-blocked test asserts the same message must NOT say these things,
                // so the two together prove the advice moves with the measurement.
                refused.Should().Contain("could not open it exclusively either",
                    "the message says how it knows, not just what it concluded");
                refused!.Should().Contain("close whatever has " + keyPath + " open");
                refused.Should().Contain("may be all this needs",
                    "a lock is the one reading under which a retry is real advice");
            }

            // The handle is gone, which is what a finished antivirus scan looks like. Nothing was
            // written to make the refusal stick, so the very next attempt gets through.
            var after = new SeatRegister(
                Bundle(), NullLogger<SeatRegister>.Instance, dbPath: dbPath, keyPath: keyPath);
            after.ClaimOnProbe(Instance("POSTUREB2"), "POSTUREB2").Allowed.Should().BeTrue(
                "the refusal must not brick an install over a file that was briefly held open");

            Directory.GetFiles(_tempDir, "seats.key.corrupt-*").Should().ContainSingle()
                .Which.Should().Match(p => File.ReadAllBytes(p).SequenceEqual(unreadable));
        }

        // ── (c) An unproven replacement means REFUSE ────────────────────────

        [Fact]
        public void A_replacement_key_that_cannot_be_proven_readable_does_not_become_the_key_in_force()
        {
            var keyPath = Path.Combine(_tempDir, "seats.key");
            var dbPath = Path.Combine(_tempDir, "seats.db");

            var unreadable = WrappedForNobody();
            File.WriteAllBytes(keyPath, unreadable);

            // The handle is taken between the write and the proof, so the read-back really fails.
            using var logger = new HandleGrabbingLogger("Generated new seat-register HMAC key", keyPath);

            var register = new SeatRegister(Bundle(), logger, dbPath: dbPath, keyPath: keyPath);
            var verdict = register.VerifyChain();

            logger.Grabbed.Should().BeTrue("this test's premise: the read-back really was blocked");
            verdict.Should().NotBeNull();
            verdict!.Should().Contain("could not prove it reads back off disk",
                "RULED 2026-08-10: before this, a failed proof was logged at Error and the unproven "
                + "key was signed with anyway");
            verdict.Should().Contain(keyPath);
            verdict.Should().Contain("fail verification",
                "the refusal states what was at stake, in this service's own terms");

            // The aside is intact and was NOT swept, because the sweep is authorised by the proof.
            var asides = Directory.GetFiles(_tempDir, "seats.key.corrupt-*");
            asides.Should().ContainSingle();
            File.ReadAllBytes(asides[0]).Should().Equal(unreadable,
                "the refusal points the operator at the preserved file, so it had better be there");

            logger.Lines.Should().Contain(l => l.Contains("Kept ") && l.Contains(asides[0]),
                "the reconcile runs before the refusal precisely so the kept files are named in the "
                + "log the refusal tells the operator to read");
        }

        [Fact]
        public void The_unproven_refusal_is_per_attempt_and_the_next_call_uses_the_written_key()
        {
            var keyPath = Path.Combine(_tempDir, "seats.key");
            var dbPath = Path.Combine(_tempDir, "seats.db");

            File.WriteAllBytes(keyPath, WrappedForNobody());

            byte[] written;
            using (var logger = new HandleGrabbingLogger("Generated new seat-register HMAC key", keyPath))
            {
                new SeatRegister(Bundle(), logger, dbPath: dbPath, keyPath: keyPath)
                    .VerifyChain().Should().Contain("could not prove it reads back off disk");
                logger.Grabbed.Should().BeTrue("this test's premise: the read-back really was blocked");

                logger.Release();
                written = File.ReadAllBytes(keyPath);
            }

            written.Should().NotBeEmpty();

            var after = new SeatRegister(
                Bundle(), NullLogger<SeatRegister>.Instance, dbPath: dbPath, keyPath: keyPath);
            after.ClaimOnProbe(Instance("POSTUREC2"), "POSTUREC2").Allowed.Should().BeTrue(
                "the key that could not be read back a moment ago reads fine now, so the service "
                + "runs on it rather than staying refused forever");
            after.VerifyChain().Should().BeNull("the chain signed under that key verifies");

            File.ReadAllBytes(keyPath).Should().Equal(written,
                "the second attempt found a readable key and did not regenerate over it");
        }
    }
}
