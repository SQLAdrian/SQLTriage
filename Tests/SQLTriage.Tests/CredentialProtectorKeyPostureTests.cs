/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// What the CENSUS for posture (a) established about <see cref="CredentialProtector"/>, pinned so
    /// the claim in its source comment is conditioned on a measurement rather than asserted.
    ///
    /// <para>The wrong-length ruling (Adrian, 2026-08-10) applies to branches that REGENERATE over
    /// material DPAPI handed back. <c>GetOrCreateAesKey</c> has no such branch, because it has no
    /// length gate at all: it returns whatever length it unwrapped. Adding a gate would have created
    /// the destructive branch the ruling exists to remove, so none was added, and what the absence
    /// costs instead is measured here.</para>
    ///
    /// <para>What the absence costs is NOT what the first draft of this file asserted. Measured
    /// 2026-08-10: AES-GCM refuses a key that is not a valid AES length, and ACCEPTS 16 and 24 bytes
    /// as AES-128 and AES-192. So the honest statement is that a non-AES length is refused at the
    /// point of use and an AES length is used at whatever strength it carries. Both legs are below.</para>
    ///
    /// <para>The measurement is taken on <see cref="AesGcmHelper"/>, which is where a wrong-length
    /// credential key ends up. It is NOT taken by driving <c>CredentialProtector</c> itself: that
    /// class resolves its key file from <c>AppContext.BaseDirectory</c>, which is the test host's own
    /// config directory, and rotating it there orphaned other stores and turned a green run red once
    /// already (recorded in <see cref="AsideProducerCensusTests"/>). So this pins the CONSEQUENCE of
    /// the missing gate, and it does not claim to have driven the class that reaches it.</para>
    /// </summary>
    public sealed class CredentialProtectorKeyPostureTests
    {
        private static byte[] Key(int lengthBytes) =>
            Enumerable.Repeat((byte)0x4B, lengthBytes).ToArray();

        [Theory]
        [InlineData(0)]
        [InlineData(8)]
        [InlineData(31)]
        [InlineData(33)]
        [InlineData(64)]
        public void A_non_AES_length_credential_key_is_refused_where_it_is_USED_not_where_it_is_read(int length)
        {
            var act = () => AesGcmHelper.Encrypt(Encoding.UTF8.GetBytes("secret"), Key(length));

            act.Should().Throw<CryptographicException>(
                "the key length is enforced by AES-GCM itself, at the point of use. That is why the "
                + "read path needs no length gate, and why adding one would only introduce a branch "
                + "that regenerates over readable key material");
        }

        [Theory]
        [InlineData(16)]
        [InlineData(24)]
        [InlineData(32)]
        public void An_AES_length_credential_key_is_ACCEPTED_including_two_this_build_never_mints(int length)
        {
            // MEASURED 2026-08-10, and it falsified the first draft of the theory above, which
            // asserted that anything other than 32 bytes throws. AesGcm takes any valid AES key
            // length, so a 16 or 24-byte credential key file would be used as AES-128 or AES-192
            // rather than refused. Nothing in this build writes one (GetOrCreateAesKey mints 32
            // bytes), so this is stated as a measurement and NOT as a defect claim: whether the
            // credential key should be pinned to 32 bytes at the point of use is a ruling nobody
            // has taken, and taking it here would be inventing one.
            var act = () => AesGcmHelper.Encrypt(Encoding.UTF8.GetBytes("secret"), Key(length));

            act.Should().NotThrow();
        }

        [Fact]
        public void The_refusal_this_class_raises_is_catchable_by_the_contract_it_already_has()
        {
            // CredentialProtector.Encrypt catches Exception from the AES path and falls back to
            // DPAPI, and SqliteCipherHelper's callers already handle IOException. Both of those hold
            // for this refusal only because of where it sits in the hierarchy, so the hierarchy is
            // pinned rather than left to a reader of two other files.
            typeof(KeyAsideRefusedException).Should().BeDerivedFrom<System.IO.IOException>(
                "the store-level refusal in SqliteCipherHelper is an IOException, and its callers "
                + "need no new catch for the key-level one");
        }
    }
}
