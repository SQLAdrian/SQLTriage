/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests.Licensing
{
    /// <summary>
    /// The key-aside lifecycle driven END TO END through the real <see cref="SeatRegister"/>: a real
    /// unreadable key file on disk, the real SQLCipher store, the real DPAPI calls, and the service's
    /// own regeneration path. <see cref="SQLTriage.Tests.KeyAsideLifecycleTests"/> drives the
    /// lifecycle's contract directly; this proves the service is actually WIRED to it, which no
    /// source-level assertion can establish.
    ///
    /// <para>The unreadable key is produced the only honest way: protected under an entropy this
    /// service does not hold, so the real <c>ProtectedData.Unprotect</c> on this machine really does
    /// throw. Nothing is stubbed and no seam is added to the service to make this reachable.</para>
    /// </summary>
    public sealed class SeatRegisterKeyAsideTests : IDisposable
    {
        private readonly string _tempDir;

        public SeatRegisterKeyAsideTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "seat-key-aside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_tempDir, recursive: true); } catch (Exception) { /* test cleanup */ }
        }

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

        [Fact]
        public void An_unreadable_seat_key_is_set_aside_kept_and_a_stale_empty_aside_is_swept()
        {
            var keyPath = Path.Combine(_tempDir, "seats.key");
            var dbPath = Path.Combine(_tempDir, "seats.db");

            // (1) A key file this machine's DPAPI will really refuse: sealed under an entropy the
            // service does not present. Same scope, same machine, same account.
            var foreign = ProtectedData.Protect(
                Enumerable.Repeat((byte)0x5A, 32).ToArray(),
                Encoding.UTF8.GetBytes("not-the-seat-register-entropy"),
                DataProtectionScope.LocalMachine);
            File.WriteAllBytes(keyPath, foreign);

            // (2) A zero-byte aside left by a previous run. This is the population a measurement can
            // prove worthless: File.WriteAllBytes truncates before it writes, so a crash between the
            // two leaves exactly this, and the next start sets it aside unreadable.
            var staleEmpty = keyPath + ".corrupt-20200101000000";
            File.WriteAllBytes(staleEmpty, Array.Empty<byte>());

            // (3) A previous run's aside that DPAPI refuses. It must survive.
            var staleSealed = keyPath + ".corrupt-20200101000001";
            File.WriteAllBytes(staleSealed, foreign);

            var register = new SeatRegister(
                Bundle(), NullLogger<SeatRegister>.Instance, dbPath: dbPath, keyPath: keyPath);
            register.ClaimOnProbe(Instance("SEATKEY1"), "SEATKEY1").Allowed.Should().BeTrue();

            // The unreadable key was moved aside, not overwritten in place.
            var asides = Directory.GetFiles(_tempDir, "seats.key.corrupt-*");
            asides.Should().Contain(staleSealed,
                "an aside DPAPI refuses is kept: the refusal may be transient and it is the only copy");
            asides.Should().NotContain(staleEmpty,
                "zero bytes hold no key material at all, and the replacement was proven before the sweep ran");
            File.Exists(staleEmpty).Should().BeFalse();

            var fresh = asides.Where(a => a != staleSealed).ToList();
            fresh.Should().ContainSingle("the run's own unreadable key was set aside exactly once");
            File.ReadAllBytes(fresh[0]).Should().Equal(foreign,
                "the aside holds the ORIGINAL bytes, unchanged: it is a move, not a rewrite");

            // The live key file was replaced and is not the file that would not unwrap.
            File.Exists(keyPath).Should().BeTrue();
            File.ReadAllBytes(keyPath).Should().NotEqual(foreign);
        }

        [Fact]
        public void A_readable_seat_key_produces_no_aside_and_no_sweep()
        {
            var keyPath = Path.Combine(_tempDir, "clean.key");
            var dbPath = Path.Combine(_tempDir, "clean.db");

            var register = new SeatRegister(
                Bundle(), NullLogger<SeatRegister>.Instance, dbPath: dbPath, keyPath: keyPath);
            register.ClaimOnProbe(Instance("SEATKEY2"), "SEATKEY2").Allowed.Should().BeTrue();

            // A first run mints a key and has nothing to reconcile. A decoy aside is planted to prove
            // the sweep did not run: if the reconcile fired on the happy path it would classify this
            // zero-byte file as worthless and delete it.
            var decoy = keyPath + ".corrupt-20200101000000";
            File.WriteAllBytes(decoy, Array.Empty<byte>());

            var reopened = new SeatRegister(
                Bundle(), NullLogger<SeatRegister>.Instance, dbPath: dbPath, keyPath: keyPath);
            reopened.ClaimOnProbe(Instance("SEATKEY3"), "SEATKEY3").Allowed.Should().BeTrue();

            File.Exists(decoy).Should().BeTrue(
                "the sweep is on the REGENERATION path only; a readable key means nothing was replaced "
                + "and nothing may be judged redundant against it");
            Directory.GetFiles(_tempDir, "clean.key.corrupt-*").Should().ContainSingle()
                .Which.Should().Be(decoy, "no new aside was produced by a key that unwraps");
        }
    }
}
