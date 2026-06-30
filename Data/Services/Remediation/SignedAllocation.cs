/* In the name of God, the Merciful, the Compassionate */
/*
 * SignedAllocation + SignedAllocationVerifier — the in-repo VERIFICATION half of the
 * redeem-signed-allocation-file feature. The app embeds only the ECDSA P-256 PUBLIC key;
 * the matching PRIVATE key is held OFF-GITHUB by the licensing issuer. So the app can VERIFY
 * a credit-grant artifact but can NEVER mint one — that is the moat (build-time absence of the
 * signing capability, not key secrecy).
 *
 * Artifact (JSON) the issuer produces and the operator redeems:
 *   {
 *     "server":     "SQLBOX\\PROD",          // exact server name the grant applies to
 *     "credits":    50,                       // integer > 0
 *     "issuedUtc":  "2026-06-24T12:00:00Z",   // ISO-8601 UTC
 *     "expiresUtc": "2027-06-24T12:00:00Z",   // ISO-8601 UTC; redeem after this is rejected
 *     "nonce":      "<unique>",               // unique per grant — replay-prevention key
 *     "signature":  "<base64>"                // see below
 *   }
 *
 * Signature = ECDSA P-256 over SHA-256 of the CANONICAL payload, DER (Rfc3279DerSequence)
 * encoded, base64. Canonical payload (LF-separated, exact JSON field values):
 *     server + "\n" + credits + "\n" + issuedUtc + "\n" + expiresUtc + "\n" + nonce
 * (credits as its decimal-integer string; the timestamp/server strings byte-for-byte as in the JSON).
 */

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>A verified credit-grant allocation (parsed from a signed artifact).</summary>
    public sealed class SignedAllocation
    {
        public string Server { get; init; } = string.Empty;
        public int Credits { get; init; }
        public DateTime IssuedUtc { get; init; }
        public DateTime ExpiresUtc { get; init; }
        public string Nonce { get; init; } = string.Empty;
    }

    public sealed class SignedAllocationResult
    {
        public bool Valid { get; init; }
        public string? Error { get; init; }
        public SignedAllocation? Allocation { get; init; }
        public static SignedAllocationResult Fail(string e) => new() { Valid = false, Error = e };
        public static SignedAllocationResult Ok(SignedAllocation a) => new() { Valid = true, Allocation = a };
    }

    public static class SignedAllocationVerifier
    {
        // ECDSA P-256 PUBLIC key (SubjectPublicKeyInfo, base64). Generated 2026-06-24; the matching
        // PRIVATE key is held off-GitHub by the licensing issuer and is NEVER in this repo. Verify-only.
        private const string PublicKeySpkiBase64 =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJxCrtlfXBOkk8nWtrLYRIXtpVyzsA5NQSQNGtUaXF+ivfysTHY8UVUbQTbG5/32me7IfPx7G+tk+Jn60vnWxKA==";

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// Verifies a signed-allocation artifact: signature against the embedded public key,
        /// non-empty server, positive credits, and not-expired (relative to <paramref name="nowUtc"/>).
        /// Replay (nonce already redeemed) is the grant store's responsibility, not this method's.
        /// </summary>
        public static SignedAllocationResult Verify(string artifactJson, DateTime nowUtc)
            => Verify(artifactJson, nowUtc, PublicKeySpkiBase64);

        // Overload taking the verification key — internal so tests sign with a THROWAWAY keypair
        // and verify against its public key (the production private key never enters the repo).
        internal static SignedAllocationResult Verify(string artifactJson, DateTime nowUtc, string publicKeySpkiBase64)
        {
            if (string.IsNullOrWhiteSpace(artifactJson))
                return SignedAllocationResult.Fail("Empty allocation file.");

            string? server, issuedRaw, expiresRaw, nonce, sigB64;
            int credits;
            try
            {
                using var doc = JsonDocument.Parse(artifactJson);
                var root = doc.RootElement;
                server = GetString(root, "server");
                issuedRaw = GetString(root, "issuedUtc");
                expiresRaw = GetString(root, "expiresUtc");
                nonce = GetString(root, "nonce");
                sigB64 = GetString(root, "signature");
                credits = root.TryGetProperty("credits", out var c) && c.TryGetInt32(out var ci) ? ci : -1;
            }
            catch (Exception ex)
            {
                return SignedAllocationResult.Fail($"Not a valid allocation file: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(server)) return SignedAllocationResult.Fail("Allocation has no server.");
            if (credits <= 0) return SignedAllocationResult.Fail("Allocation credits must be a positive integer.");
            if (string.IsNullOrWhiteSpace(issuedRaw) || string.IsNullOrWhiteSpace(expiresRaw))
                return SignedAllocationResult.Fail("Allocation is missing issued/expiry timestamps.");
            if (string.IsNullOrWhiteSpace(nonce)) return SignedAllocationResult.Fail("Allocation has no nonce.");
            if (string.IsNullOrWhiteSpace(sigB64)) return SignedAllocationResult.Fail("Allocation is not signed.");

            // Canonical payload — must match exactly what the issuer signed (raw field values).
            var canonical = $"{server}\n{credits.ToString(CultureInfo.InvariantCulture)}\n{issuedRaw}\n{expiresRaw}\n{nonce}";

            byte[] sig;
            try { sig = Convert.FromBase64String(sigB64); }
            catch { return SignedAllocationResult.Fail("Signature is not valid base64."); }

            bool ok;
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
                ok = ec.VerifyData(Encoding.UTF8.GetBytes(canonical), sig,
                                   HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            catch (Exception ex)
            {
                return SignedAllocationResult.Fail($"Signature verification error: {ex.Message}");
            }
            if (!ok)
                return SignedAllocationResult.Fail("Signature does not verify against the licensing key — file is forged, corrupt, or for a different product.");

            if (!DateTime.TryParse(issuedRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var issued)
                || !DateTime.TryParse(expiresRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expires))
                return SignedAllocationResult.Fail("Allocation timestamps are not valid ISO-8601.");

            if (nowUtc > expires)
                return SignedAllocationResult.Fail($"Allocation expired on {expires:u} (signed, but past its expiry).");

            return SignedAllocationResult.Ok(new SignedAllocation
            {
                Server = server!.Trim(),
                Credits = credits,
                IssuedUtc = issued,
                ExpiresUtc = expires,
                Nonce = nonce!.Trim(),
            });
        }

        private static string? GetString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }
}
