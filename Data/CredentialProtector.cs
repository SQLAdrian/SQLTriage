/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;


#pragma warning disable CA1416 // Windows-only API — project targets net8.0-windows
namespace SQLTriage.Data
{
    /// <summary>
    /// Encrypts and decrypts sensitive strings using layered encryption:
    ///   "enc:"  — DPAPI CurrentUser scope (interactive user, original)
    ///   "aes:"  — AES-256-GCM with DPAPI-protected machine key (works for services + any user)
    ///
    /// Decryption tries all formats automatically.
    /// New encryptions use AES-256-GCM by default (strongest, cross-account on same machine).
    /// </summary>
    public static class CredentialProtector
    {
        private static readonly byte[] AppEntropy =
            Encoding.UTF8.GetBytes("SQLTriage.SQLTriage.v1");

        // AES key file location — next to the exe, protected by NTFS + DPAPI machine scope
        private static readonly string KeyFilePath =
            Path.Combine(AppContext.BaseDirectory, "config", ".credential-key");

        // ────────────────────────────────────────────────────────────────
        //  Public API
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Encrypts a plaintext string using AES-256-GCM (preferred) with fallback to DPAPI.
        /// </summary>
        public static string Encrypt(string? plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            try
            {
                return EncryptAesGcm(plainText);
            }
            catch (Exception aesEx)
            {
                Serilog.Log.Warning(aesEx, "[CredentialProtector] AES-GCM encryption failed, falling back to DPAPI");
                // Fallback to DPAPI CurrentUser if AES fails
                try { return EncryptDpapi(plainText, DataProtectionScope.CurrentUser, "enc:"); }
                catch (Exception dpapiEx)
                {
                    Serilog.Log.Error(dpapiEx, "[CredentialProtector] Both AES-GCM and DPAPI encryption failed — credential will NOT be saved");
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// Decrypts a string. Auto-detects format: aes:, enc:, or legacy plaintext.
        /// </summary>
        public static string Decrypt(string? encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText))
                return string.Empty;

            // AES-256-GCM (preferred)
            if (encryptedText.StartsWith("aes:", StringComparison.Ordinal))
                return DecryptAesGcm(encryptedText);

            // DPAPI CurrentUser (legacy)
            if (encryptedText.StartsWith("enc:", StringComparison.Ordinal))
                return DecryptDpapi(encryptedText, "enc:");

            // Legacy plaintext — will be re-encrypted on next save
            Serilog.Log.Warning("Legacy plaintext credential detected — will be re-encrypted on next save");
            return encryptedText;
        }

        /// <summary>
        /// Returns true if the value is already encrypted (any supported format).
        /// </summary>
        public static bool IsEncrypted(string? value)
        {
            return value != null && (
                value.StartsWith("enc:", StringComparison.Ordinal) ||
                value.StartsWith("aes:", StringComparison.Ordinal));
        }

        // ────────────────────────────────────────────────────────────────
        //  AES-256-GCM  (96-bit nonce, 128-bit tag, 256-bit key)
        // ────────────────────────────────────────────────────────────────

        private static string EncryptAesGcm(string plainText)
        {
            var key = GetOrCreateAesKey();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var result = AesGcmHelper.Encrypt(plainBytes, key);
            return "aes:" + Convert.ToBase64String(result);
        }

        private static string DecryptAesGcm(string encryptedText)
        {
            try
            {
                var key = GetOrCreateAesKey();
                var data = Convert.FromBase64String(encryptedText.Substring(4));
                var plainBytes = AesGcmHelper.Decrypt(data, key);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (CryptographicException ex)
            {
                Serilog.Log.Warning(ex, "[CredentialProtector] AES-GCM decryption failed — credential may be corrupted or tampered with");
                return string.Empty;
            }
            catch (FormatException ex)
            {
                Serilog.Log.Warning(ex, "[CredentialProtector] AES-GCM decryption failed — invalid Base64 format");
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets or creates a 256-bit AES key, stored on disk protected by DPAPI LocalMachine scope.
        /// This allows any process on the machine to use the key (including Windows Services).
        /// </summary>
        private static byte[] GetOrCreateAesKey()
        {
            if (File.Exists(KeyFilePath))
            {
                try
                {
                    var protectedKey = File.ReadAllBytes(KeyFilePath);
                    return ProtectedData.Unprotect(protectedKey, AppEntropy, DataProtectionScope.LocalMachine);
                }
                catch (CryptographicException)
                {
                    // Key was created by different machine or corrupted — regenerate
                }
            }

            // Generate new 256-bit key
            var newKey = new byte[32];
            RandomNumberGenerator.Fill(newKey);

            // Protect with DPAPI LocalMachine scope (any user/service on this machine can decrypt)
            var protectedBytes = ProtectedData.Protect(newKey, AppEntropy, DataProtectionScope.LocalMachine);

            var dir = Path.GetDirectoryName(KeyFilePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(KeyFilePath, protectedBytes);

            // Restrict file permissions (best-effort — NTFS ACL)
            try
            {
                var fileInfo = new FileInfo(KeyFilePath);
                fileInfo.Attributes |= FileAttributes.Hidden;
            }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[CredentialProtector] Failed to set hidden attribute on key file"); }

            return newKey;
        }

        // ────────────────────────────────────────────────────────────────
        //  DPAPI (legacy, kept for backward compatibility)
        // ────────────────────────────────────────────────────────────────

        private static string EncryptDpapi(string plainText, DataProtectionScope scope, string prefix)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] encryptedBytes = ProtectedData.Protect(plainBytes, AppEntropy, scope);
            return prefix + Convert.ToBase64String(encryptedBytes);
        }

        private static string DecryptDpapi(string encryptedText, string prefix)
        {
            try
            {
                string base64 = encryptedText.Substring(prefix.Length);
                byte[] encryptedBytes = Convert.FromBase64String(base64);

                // Try CurrentUser first (original), then LocalMachine (service mode)
                try
                {
                    byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, AppEntropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(decryptedBytes);
                }
                catch (CryptographicException)
                {
                    byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, AppEntropy, DataProtectionScope.LocalMachine);
                    return Encoding.UTF8.GetString(decryptedBytes);
                }
            }
            catch (CryptographicException ex)
            {
                Serilog.Log.Warning(ex, "[CredentialProtector] DPAPI decryption failed — credential may be from a different machine or user");
                return string.Empty;
            }
            catch (FormatException ex)
            {
                Serilog.Log.Warning(ex, "[CredentialProtector] DPAPI decryption failed — invalid Base64 format, returning raw value");
                return encryptedText;
            }
        }
    }
}
#pragma warning restore CA1416
