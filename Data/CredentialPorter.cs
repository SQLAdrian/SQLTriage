/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SQLTriage.Data.Models;

#pragma warning disable CA1416 // Windows-only API — project targets net8.0-windows
namespace SQLTriage.Data
{
    /// <summary>
    /// Exports and imports server credentials as a portable encrypted bundle.
    /// Credentials are decrypted from the machine-scoped AES key, then re-encrypted
    /// with a user-supplied passphrase (PBKDF2 → AES-256-GCM) for the export file.
    /// On import the reverse happens: passphrase decrypts, then CredentialProtector
    /// re-encrypts with the target machine's key.
    ///
    /// File format: JSON with a "salt" (base64) and per-server "encPassword" (base64).
    /// File extension: .lmcreds
    /// </summary>
    public static class CredentialPorter
    {
        private const int SaltBytes     = 32;
        private const int KeyBytes      = 32;   // AES-256
        private const int Pbkdf2Iters   = 310_000; // OWASP 2023 minimum for PBKDF2-HMAC-SHA256

        // ──────────────────────────────────────────────────────────────────
        //  Export
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a portable credential bundle from the current server list.
        /// Returns the JSON string to be saved as a .lmcreds file.
        /// Throws if passphrase is blank or any decryption fails.
        /// </summary>
        public static string Export(IEnumerable<ServerConnection> connections, string passphrase)
        {
            if (string.IsNullOrWhiteSpace(passphrase))
                throw new ArgumentException("Passphrase must not be empty.", nameof(passphrase));

            var salt = new byte[SaltBytes];
            RandomNumberGenerator.Fill(salt);
            var passphraseKey = DeriveKey(passphrase, salt);

            var entries = new List<PortableServerEntry>();
            foreach (var conn in connections)
            {
                string? encPassword = null;
                if (!string.IsNullOrEmpty(conn.Password) && !conn.UseWindowsAuthentication)
                {
                    var plain = conn.GetDecryptedPassword();
                    if (string.IsNullOrEmpty(plain))
                        throw new CryptographicException(
                            $"Failed to decrypt password for server '{conn.ServerNames}'. " +
                            "Cannot export credentials that cannot be read on this machine.");

                    var plainBytes = Encoding.UTF8.GetBytes(plain);
                    var encrypted  = AesGcmHelper.Encrypt(plainBytes, passphraseKey);
                    encPassword    = Convert.ToBase64String(encrypted);
                }

                entries.Add(new PortableServerEntry
                {
                    Id                     = conn.Id,
                    ServerNames            = conn.ServerNames,
                    UseWindowsAuth         = conn.UseWindowsAuthentication,
                    Username               = conn.Username,
                    EncPassword            = encPassword,
                    Tags                   = conn.Tags,
                    HasSqlWatch            = conn.HasSqlWatch,
                    IsEnabled              = conn.IsEnabled,
                    Environment            = conn.Environment,
                    TrustServerCertificate = conn.TrustServerCertificate,
                    Database               = conn.Database,
                    MultiSubnetFailover    = conn.MultiSubnetFailover,
                    ConnectionTimeout      = conn.ConnectionTimeout,
                });
            }

            var bundle = new CredentialBundle
            {
                ExportedAt    = DateTime.UtcNow,
                ExportedFrom  = Environment.MachineName,
                Salt          = Convert.ToBase64String(salt),
                Servers       = entries,
            };

            return JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
        }

        // ──────────────────────────────────────────────────────────────────
        //  Import
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses a .lmcreds bundle and returns a list of <see cref="ServerConnection"/> objects
        /// with passwords re-encrypted using this machine's AES key.
        /// Throws <see cref="CryptographicException"/> on wrong passphrase.
        /// </summary>
        public static List<ServerConnection> Import(string json, string passphrase)
        {
            if (string.IsNullOrWhiteSpace(passphrase))
                throw new ArgumentException("Passphrase must not be empty.", nameof(passphrase));

            CredentialBundle bundle;
            try
            {
                bundle = JsonSerializer.Deserialize<CredentialBundle>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new FormatException("Invalid .lmcreds file — could not parse JSON.");
            }
            catch (JsonException ex)
            {
                throw new FormatException($"Invalid .lmcreds file: {ex.Message}", ex);
            }

            var salt          = Convert.FromBase64String(bundle.Salt ?? throw new FormatException("Missing salt in .lmcreds file."));
            var passphraseKey = DeriveKey(passphrase, salt);

            var result = new List<ServerConnection>();
            foreach (var entry in bundle.Servers ?? [])
            {
                var conn = new ServerConnection
                {
                    Id                       = entry.Id ?? Guid.NewGuid().ToString(),
                    ServerNames              = entry.ServerNames ?? "",
                    UseWindowsAuthentication = entry.UseWindowsAuth,
                    Username                 = entry.Username,
                    Tags                     = entry.Tags ?? [],
                    HasSqlWatch              = entry.HasSqlWatch,
                    IsEnabled                = entry.IsEnabled,
                    Environment              = entry.Environment,
                    TrustServerCertificate   = entry.TrustServerCertificate,
                    Database                 = entry.Database ?? "master",
                    MultiSubnetFailover      = entry.MultiSubnetFailover,
                    ConnectionTimeout        = entry.ConnectionTimeout > 0 ? entry.ConnectionTimeout : 15,
                };

                if (!string.IsNullOrEmpty(entry.EncPassword) && !entry.UseWindowsAuth)
                {
                    byte[] encrypted;
                    try
                    {
                        encrypted = Convert.FromBase64String(entry.EncPassword);
                    }
                    catch (FormatException)
                    {
                        throw new CryptographicException($"Credential data for '{conn.ServerNames}' is corrupt.");
                    }

                    // This throws CryptographicException if the passphrase is wrong
                    var plainBytes = AesGcmHelper.Decrypt(encrypted, passphraseKey);
                    var plain      = Encoding.UTF8.GetString(plainBytes);

                    // Re-encrypt with this machine's key
                    conn.SetPassword(plain);
                }

                result.Add(conn);
            }

            return result;
        }

        // ──────────────────────────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────────────────────────

        private static byte[] DeriveKey(string passphrase, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(passphrase),
                salt,
                Pbkdf2Iters,
                HashAlgorithmName.SHA256,
                KeyBytes);

        // ──────────────────────────────────────────────────────────────────
        //  DTOs (private — only used for serialisation)
        // ──────────────────────────────────────────────────────────────────

        private sealed class CredentialBundle
        {
            [JsonPropertyName("exportedAt")]    public DateTime ExportedAt   { get; set; }
            [JsonPropertyName("exportedFrom")]  public string?  ExportedFrom { get; set; }
            [JsonPropertyName("salt")]          public string?  Salt         { get; set; }
            [JsonPropertyName("servers")]       public List<PortableServerEntry>? Servers { get; set; }
        }

        private sealed class PortableServerEntry
        {
            [JsonPropertyName("id")]                    public string?       Id                     { get; set; }
            [JsonPropertyName("serverNames")]           public string?       ServerNames            { get; set; }
            [JsonPropertyName("useWindowsAuth")]        public bool          UseWindowsAuth         { get; set; }
            [JsonPropertyName("username")]              public string?       Username               { get; set; }
            [JsonPropertyName("encPassword")]           public string?       EncPassword            { get; set; }
            [JsonPropertyName("tags")]                  public List<string>? Tags                   { get; set; }
            [JsonPropertyName("hasSqlWatch")]           public bool          HasSqlWatch            { get; set; }
            [JsonPropertyName("isEnabled")]             public bool          IsEnabled              { get; set; }
            [JsonPropertyName("environment")]           public string?       Environment            { get; set; }
            [JsonPropertyName("trustServerCertificate")]public bool          TrustServerCertificate { get; set; }
            [JsonPropertyName("database")]              public string?       Database               { get; set; }
            [JsonPropertyName("multiSubnetFailover")]   public bool          MultiSubnetFailover    { get; set; }
            [JsonPropertyName("connectionTimeout")]     public int           ConnectionTimeout      { get; set; }
        }
    }
}
#pragma warning restore CA1416
