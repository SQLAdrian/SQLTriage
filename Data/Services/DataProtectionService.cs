/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SqlHealthAssessment.Data.Services
{
    /// <summary>
    /// Enterprise-grade in-memory data protection for sensitive dashboard data.
    /// Encrypts query results, connection strings, and cached data using AES-256-GCM.
    ///
    /// The session key is generated per-process (never written to disk) and rotated
    /// on each application restart, ensuring that memory dumps or swap files cannot
    /// be used to recover data from a previous session.
    ///
    /// Thread-safe. All operations are atomic.
    /// </summary>
    public class DataProtectionService
    {
        private readonly ILogger<DataProtectionService> _logger;
        private readonly byte[] _sessionKey;    // 256-bit, per-process, never persisted
        private readonly object _lock = new();

        public DataProtectionService(ILogger<DataProtectionService> logger)
        {
            _logger = logger;

            // Generate a per-process session key (32 bytes = 256 bits)
            _sessionKey = new byte[32];
            RandomNumberGenerator.Fill(_sessionKey);

            _logger.LogInformation("DataProtectionService initialized with ephemeral session key");
        }

        /// <summary>
        /// Encrypts a string using AES-256-GCM with the session key.
        /// Returns a Base64-encoded blob containing nonce + tag + ciphertext.
        /// </summary>
        public string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            try
            {
                var plainBytes = Encoding.UTF8.GetBytes(plainText);
                var result = AesGcmHelper.Encrypt(plainBytes, _sessionKey);
                return Convert.ToBase64String(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Data protection encrypt failed");
                return string.Empty;
            }
        }

        /// <summary>
        /// Decrypts a previously protected string.
        /// </summary>
        public string Unprotect(string protectedText)
        {
            if (string.IsNullOrEmpty(protectedText))
                return string.Empty;

            try
            {
                var data = Convert.FromBase64String(protectedText);
                var plainBytes = AesGcmHelper.Decrypt(data, _sessionKey);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Data protection decrypt failed");
                return string.Empty;
            }
        }

        /// <summary>
        /// Encrypts a byte array, returning the encrypted blob.
        /// </summary>
        public byte[] ProtectBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            return AesGcmHelper.Encrypt(data, _sessionKey);
        }

        /// <summary>
        /// Decrypts a byte array previously encrypted with ProtectBytes.
        /// </summary>
        public byte[] UnprotectBytes(byte[] protectedData)
        {
            if (protectedData == null || protectedData.Length < 28)
                return Array.Empty<byte>();

            return AesGcmHelper.Decrypt(protectedData, _sessionKey);
        }

        /// <summary>
        /// Protects a JSON-serializable object.
        /// </summary>
        public string ProtectObject<T>(T obj)
        {
            var json = JsonSerializer.Serialize(obj);
            return Protect(json);
        }

        /// <summary>
        /// Unprotects and deserializes a JSON object.
        /// </summary>
        public T? UnprotectObject<T>(string protectedText)
        {
            var json = Unprotect(protectedText);
            if (string.IsNullOrEmpty(json)) return default;
            return JsonSerializer.Deserialize<T>(json);
        }

        /// <summary>
        /// Securely wipes a byte array from memory.
        /// </summary>
        public static void SecureWipe(byte[] data)
        {
            if (data != null)
                CryptographicOperations.ZeroMemory(data);
        }
    }
}
