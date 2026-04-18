/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Security.Cryptography;

namespace SQLTriage.Data
{
    /// <summary>
    /// Shared AES-256-GCM encrypt/decrypt primitives.
    /// Wire format: nonce(12) + tag(16) + ciphertext(N) packed into a single byte array.
    /// Used by CredentialProtector, DataProtectionService, and SqlServerConnectionFactory.
    /// </summary>
    public static class AesGcmHelper
    {
        private const int NonceSize = 12;  // 96-bit nonce (GCM standard)
        private const int TagSize = 16;    // 128-bit authentication tag
        private const int HeaderSize = NonceSize + TagSize; // 28 bytes

        /// <summary>
        /// Encrypts plaintext bytes with a 256-bit key using AES-256-GCM.
        /// Returns nonce(12) + tag(16) + ciphertext(N).
        /// </summary>
        public static byte[] Encrypt(byte[] plainBytes, byte[] key)
        {
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plainBytes, ciphertext, tag);

            var result = new byte[HeaderSize + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
            Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
            Buffer.BlockCopy(ciphertext, 0, result, HeaderSize, ciphertext.Length);

            return result;
        }

        /// <summary>
        /// Decrypts a blob produced by <see cref="Encrypt"/>.
        /// Returns the plaintext bytes, or an empty array if the blob is invalid.
        /// </summary>
        public static byte[] Decrypt(byte[] blob, byte[] key)
        {
            if (blob == null || blob.Length < HeaderSize)
                return Array.Empty<byte>();

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var ciphertext = new byte[blob.Length - HeaderSize];

            Buffer.BlockCopy(blob, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(blob, NonceSize, tag, 0, TagSize);
            Buffer.BlockCopy(blob, HeaderSize, ciphertext, 0, ciphertext.Length);

            var plainBytes = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plainBytes);

            return plainBytes;
        }
    }
}
