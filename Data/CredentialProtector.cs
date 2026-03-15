/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Security.Cryptography;
using System.Text;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Encrypts and decrypts sensitive strings (passwords, connection strings) using Windows DPAPI.
    /// Data is protected with DataProtectionScope.CurrentUser, meaning only the same Windows user
    /// who encrypted the data can decrypt it.
    /// </summary>
    public static class CredentialProtector
    {
        // Fixed app-specific entropy for DPAPI. Must be stable across restarts so that
        // passwords encrypted in one session can be decrypted in the next.
        // DPAPI already binds to the Windows user profile; this adds a secondary app-specific
        // factor so data protected by this app cannot be trivially decrypted by other processes
        // running as the same user without knowledge of this value.
        private static readonly byte[] AdditionalEntropy =
            Encoding.UTF8.GetBytes("SqlHealthAssessment.LiveMonitor.v1");

        /// <summary>
        /// Encrypts a plaintext string using DPAPI (CurrentUser scope).
        /// Returns a Base64-encoded string prefixed with "enc:" to identify encrypted values.
        /// </summary>
        /// <param name="plainText">The plaintext string to encrypt.</param>
        /// <returns>Base64-encoded encrypted string prefixed with "enc:", or empty string if input is null/empty.</returns>
        public static string Encrypt(string? plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] encryptedBytes = ProtectedData.Protect(
                    plainBytes,
                    AdditionalEntropy,
                    DataProtectionScope.CurrentUser);

                return "enc:" + Convert.ToBase64String(encryptedBytes);
            }
            catch (CryptographicException)
            {
                // If DPAPI fails (e.g., running as a service without user profile),
                // return empty to avoid storing plaintext
                return string.Empty;
            }
        }

        /// <summary>
        /// Decrypts a DPAPI-encrypted string. If the input is not prefixed with "enc:",
        /// it is treated as a legacy plaintext value and returned as-is (for migration).
        /// WARNING: This now logs a warning for security awareness.
        /// </summary>
        /// <param name="encryptedText">The encrypted string (prefixed with "enc:") or legacy plaintext.</param>
        /// <returns>The decrypted plaintext string.</returns>
        public static string Decrypt(string? encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText))
                return string.Empty;

            // If not prefixed with "enc:", treat as legacy plaintext (migration support)
            if (!encryptedText.StartsWith("enc:", StringComparison.Ordinal))
            {
                // Log warning about legacy plaintext - in production, this should trigger re-encryption
                System.Diagnostics.Debug.WriteLine(
                    "[Security] WARNING: Legacy plaintext credential detected. " +
                    "Consider re-encrypting to ensure proper security.");
                return encryptedText;
            }

            try
            {
                string base64 = encryptedText.Substring(4); // Remove "enc:" prefix
                byte[] encryptedBytes = Convert.FromBase64String(base64);
                byte[] decryptedBytes = ProtectedData.Unprotect(
                    encryptedBytes,
                    AdditionalEntropy,
                    DataProtectionScope.CurrentUser);

                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (CryptographicException)
            {
                // Decryption failed - possibly encrypted by a different user or corrupted
                return string.Empty;
            }
            catch (FormatException)
            {
                // Invalid Base64 - treat as legacy plaintext
                return encryptedText;
            }
        }

        /// <summary>
        /// Returns true if the value appears to be already encrypted (prefixed with "enc:").
        /// </summary>
        public static bool IsEncrypted(string? value)
        {
            return value != null && value.StartsWith("enc:", StringComparison.Ordinal);
        }
    }
}
