/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Redeem-signed-allocation-file: the app VERIFIES a credit grant against an embedded public
    /// key but can never ISSUE one (the private key is off-GitHub). These tests sign with a
    /// THROWAWAY keypair (never the production private key) and pin: a valid grant verifies; a
    /// tampered / wrong-key / expired grant is rejected; the grant store is replay-proof and only
    /// counts non-expired grants.
    /// </summary>
    public class RemediationGrantRedeemTests : IDisposable
    {
        private readonly ECDsa _signer = ECDsa.Create(ECCurve.CreateFromFriendlyName("nistP256"));
        private string PublicKeyB64 => Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo());

        public void Dispose() => _signer.Dispose();

        // Builds a signed artifact with the throwaway key, using the SAME canonical form the verifier expects.
        private string SignedArtifact(string server, int credits, DateTime issuedUtc, DateTime expiresUtc, string nonce, bool corrupt = false)
        {
            string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var issued = Iso(issuedUtc);
            var expires = Iso(expiresUtc);
            var canonical = $"{server}\n{credits.ToString(CultureInfo.InvariantCulture)}\n{issued}\n{expires}\n{nonce}";
            var sig = _signer.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var sigB64 = Convert.ToBase64String(sig);
            var signedCredits = corrupt ? credits + 1000 : credits; // tamper: change credits AFTER signing
            return $@"{{ ""server"": ""{server}"", ""credits"": {signedCredits}, ""issuedUtc"": ""{issued}"", ""expiresUtc"": ""{expires}"", ""nonce"": ""{nonce}"", ""signature"": ""{sigB64}"" }}";
        }

        private static readonly DateTime Now = new(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Verify_validGrant_passes()
        {
            var json = SignedArtifact("SQLBOX", 50, Now, Now.AddYears(1), "n-1");
            var r = SignedAllocationVerifier.Verify(json, Now, PublicKeyB64);
            Assert.True(r.Valid, r.Error);
            Assert.Equal(50, r.Allocation!.Credits);
            Assert.Equal("SQLBOX", r.Allocation.Server);
        }

        [Fact]
        public void Verify_tamperedCredits_fails()
        {
            // Sign 50, then bump credits to 1050 in the JSON without re-signing.
            var json = SignedArtifact("SQLBOX", 50, Now, Now.AddYears(1), "n-2", corrupt: true);
            var r = SignedAllocationVerifier.Verify(json, Now, PublicKeyB64);
            Assert.False(r.Valid);
        }

        [Fact]
        public void Verify_wrongKey_fails()
        {
            var json = SignedArtifact("SQLBOX", 50, Now, Now.AddYears(1), "n-3");
            using var other = ECDsa.Create(ECCurve.CreateFromFriendlyName("nistP256"));
            var otherPub = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
            var r = SignedAllocationVerifier.Verify(json, Now, otherPub);
            Assert.False(r.Valid);
        }

        [Fact]
        public void Verify_expiredGrant_fails()
        {
            var json = SignedArtifact("SQLBOX", 50, Now.AddYears(-2), Now.AddYears(-1), "n-4");
            var r = SignedAllocationVerifier.Verify(json, Now, PublicKeyB64);
            Assert.False(r.Valid);
            Assert.Contains("expired", r.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Verify_productionEmbeddedKey_overloadRejectsRandomJson()
        {
            // The default (production-key) overload must reject an unsigned/foreign blob.
            var r = SignedAllocationVerifier.Verify(@"{ ""server"":""x"",""credits"":9,""issuedUtc"":""2026-06-24T00:00:00Z"",""expiresUtc"":""2027-06-24T00:00:00Z"",""nonce"":""z"",""signature"":""AAAA"" }", Now);
            Assert.False(r.Valid);
        }

        // ── Grant store: replay-proof + only non-expired count ──────────────

        private static RemediationGrantStore NewStore(out string path)
        {
            path = Path.Combine(Path.GetTempPath(), "grants-" + Guid.NewGuid().ToString("N") + ".json");
            return new RemediationGrantStore(path);
        }

        private static SignedAllocation Alloc(string server, int credits, DateTime expires, string nonce) =>
            new() { Server = server, Credits = credits, IssuedUtc = Now, ExpiresUtc = expires, Nonce = nonce };

        [Fact]
        public void GrantStore_redeem_thenReplay_isRejected()
        {
            var store = NewStore(out var path);
            try
            {
                Assert.Null(store.Redeem(Alloc("SQLBOX", 50, Now.AddYears(1), "nonce-A"), Now));
                Assert.Equal(50, store.GrantedCreditsFor("SQLBOX", Now));
                // Same nonce again → replay rejected; credits unchanged.
                Assert.NotNull(store.Redeem(Alloc("SQLBOX", 50, Now.AddYears(1), "nonce-A"), Now));
                Assert.Equal(50, store.GrantedCreditsFor("SQLBOX", Now));
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void GrantStore_expiredGrant_doesNotCount()
        {
            var store = NewStore(out var path);
            try
            {
                store.Redeem(Alloc("SQLBOX", 50, Now.AddDays(-1), "nonce-B"), Now); // already expired
                Assert.Equal(0, store.GrantedCreditsFor("SQLBOX", Now));
                Assert.Equal(0, store.GrantedCreditsFor("OTHER", Now)); // different server
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void GrantStore_persistsAcrossInstances()
        {
            var path = Path.Combine(Path.GetTempPath(), "grants-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                new RemediationGrantStore(path).Redeem(Alloc("SQLBOX", 30, Now.AddYears(1), "nonce-C"), Now);
                // A fresh instance reads the persisted grant and rejects the replay.
                var reopened = new RemediationGrantStore(path);
                Assert.Equal(30, reopened.GrantedCreditsFor("SQLBOX", Now));
                Assert.NotNull(reopened.Redeem(Alloc("SQLBOX", 30, Now.AddYears(1), "nonce-C"), Now));
            }
            finally { try { File.Delete(path); } catch { } }
        }
    }
}
