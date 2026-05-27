/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests
{
    public class CredentialPorterTests
    {
        // Static helpers --------------------------------------------------

        private static ServerConnection SqlAuthServer(string name, string password, string? user = "sa") =>
            BuildAndEncrypt(new ServerConnection
            {
                ServerNames = name,
                UseWindowsAuthentication = false,
                Username = user,
                Database = "master",
                IsEnabled = true,
                Environment = "Prod",
                Tags = new List<string> { "test" },
                HasSqlWatch = false,
                TrustServerCertificate = true,
                MultiSubnetFailover = false,
                ConnectionTimeout = 30,
            }, password);

        private static ServerConnection BuildAndEncrypt(ServerConnection conn, string password)
        {
            conn.SetPassword(password);
            return conn;
        }

        private static ServerConnection WindowsAuthServer(string name) => new()
        {
            ServerNames = name,
            UseWindowsAuthentication = true,
            Database = "master",
            IsEnabled = true,
        };

        // Round-trip ------------------------------------------------------

        [Fact]
        public void RoundTrip_SqlAuth_RecoversPasswordOnImport()
        {
            var original = SqlAuthServer("server01", "Sup3rS3cret!");
            var json = CredentialPorter.Export(new[] { original }, "correct-horse-battery-staple");

            var imported = CredentialPorter.Import(json, "correct-horse-battery-staple");

            Assert.Single(imported);
            Assert.Equal("server01", imported[0].ServerNames);
            Assert.Equal("sa", imported[0].Username);
            Assert.Equal("Sup3rS3cret!", imported[0].GetDecryptedPassword());
            // Re-encrypted on import so the on-disk blob should differ from the original
            Assert.NotEqual(original.Password, imported[0].Password);
        }

        [Fact]
        public void RoundTrip_PreservesAllPortableFields()
        {
            var original = SqlAuthServer("srv-a", "pw1");
            original.HasSqlWatch = true;
            original.TrustServerCertificate = true;
            original.MultiSubnetFailover = true;
            original.ConnectionTimeout = 42;
            original.Environment = "Staging";
            original.Tags = new List<string> { "az", "primary" };
            original.IsEnabled = false;
            original.Database = "tempdb";

            var json = CredentialPorter.Export(new[] { original }, "passphrase");
            var imported = CredentialPorter.Import(json, "passphrase")[0];

            Assert.Equal(original.Id, imported.Id);
            Assert.Equal(original.ServerNames, imported.ServerNames);
            Assert.True(imported.HasSqlWatch);
            Assert.True(imported.TrustServerCertificate);
            Assert.True(imported.MultiSubnetFailover);
            Assert.Equal(42, imported.ConnectionTimeout);
            Assert.Equal("Staging", imported.Environment);
            Assert.Equal(new[] { "az", "primary" }, imported.Tags);
            Assert.False(imported.IsEnabled);
            Assert.Equal("tempdb", imported.Database);
        }

        [Fact]
        public void RoundTrip_MultipleServers_AllRecovered()
        {
            var connections = new[]
            {
                SqlAuthServer("server-1", "alpha-pw"),
                SqlAuthServer("server-2", "beta-pw", user: "appuser"),
                SqlAuthServer("server-3", "gamma-pw", user: "monitor"),
            };

            var json = CredentialPorter.Export(connections, "ph");
            var imported = CredentialPorter.Import(json, "ph");

            Assert.Equal(3, imported.Count);
            Assert.Equal("alpha-pw", imported[0].GetDecryptedPassword());
            Assert.Equal("beta-pw", imported[1].GetDecryptedPassword());
            Assert.Equal("gamma-pw", imported[2].GetDecryptedPassword());
            Assert.Equal(new[] { "sa", "appuser", "monitor" }, imported.Select(c => c.Username).ToArray());
        }

        [Fact]
        public void RoundTrip_WindowsAuthServer_HasNoEncryptedPassword()
        {
            var winAuth = WindowsAuthServer("dom-srv");
            var sqlAuth = SqlAuthServer("sql-srv", "sql-pw");

            var json = CredentialPorter.Export(new[] { winAuth, sqlAuth }, "ph");
            using var doc = JsonDocument.Parse(json);
            var servers = doc.RootElement.GetProperty("servers");

            // The Windows-auth entry must carry no encPassword payload
            var win = servers[0];
            Assert.True(win.GetProperty("useWindowsAuth").GetBoolean());
            // encPassword is null (or absent) — System.Text.Json serialises null by default
            Assert.True(win.GetProperty("encPassword").ValueKind == JsonValueKind.Null);

            var sql = servers[1];
            Assert.False(sql.GetProperty("useWindowsAuth").GetBoolean());
            Assert.Equal(JsonValueKind.String, sql.GetProperty("encPassword").ValueKind);

            var imported = CredentialPorter.Import(json, "ph");
            Assert.True(imported[0].UseWindowsAuthentication);
            Assert.Null(imported[0].Password);
            Assert.Equal("sql-pw", imported[1].GetDecryptedPassword());
        }

        [Fact]
        public void Export_EmptyList_ProducesValidBundle_ImportReturnsEmpty()
        {
            var json = CredentialPorter.Export(Array.Empty<ServerConnection>(), "ph");
            var imported = CredentialPorter.Import(json, "ph");
            Assert.Empty(imported);
        }

        [Fact]
        public void Export_SetsMachineNameAndTimestamp()
        {
            var before = DateTime.UtcNow.AddSeconds(-1);
            var json = CredentialPorter.Export(new[] { SqlAuthServer("s", "p") }, "ph");
            var after = DateTime.UtcNow.AddSeconds(1);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var exportedAt = root.GetProperty("exportedAt").GetDateTime();
            Assert.InRange(exportedAt, before, after);
            Assert.Equal(Environment.MachineName, root.GetProperty("exportedFrom").GetString());
        }

        // Salt & nonce randomness ----------------------------------------

        [Fact]
        public void Export_TwoCallsSameData_ProduceDifferentCiphertexts()
        {
            // Salt and AES-GCM nonce are random — exporting identical data twice with the
            // same passphrase must NEVER produce identical bytes (catches regressions where
            // a contributor accidentally seeds the salt with a constant).
            var conn = SqlAuthServer("s", "p");
            var a = CredentialPorter.Export(new[] { conn }, "ph");
            var b = CredentialPorter.Export(new[] { conn }, "ph");
            Assert.NotEqual(a, b);

            // Both must still decrypt back to "p"
            Assert.Equal("p", CredentialPorter.Import(a, "ph")[0].GetDecryptedPassword());
            Assert.Equal("p", CredentialPorter.Import(b, "ph")[0].GetDecryptedPassword());
        }

        [Fact]
        public void Export_SameData_ProducesDifferentSalts()
        {
            var conn = SqlAuthServer("s", "p");
            var saltA = ParseField(CredentialPorter.Export(new[] { conn }, "ph"), "salt");
            var saltB = ParseField(CredentialPorter.Export(new[] { conn }, "ph"), "salt");
            Assert.NotEqual(saltA, saltB);

            // And the salt must be at least 32 bytes (per SaltBytes const)
            Assert.True(Convert.FromBase64String(saltA!).Length >= 32);
        }

        // Wrong passphrase / tampering -----------------------------------

        [Fact]
        public void Import_WrongPassphrase_ThrowsCryptographicException()
        {
            var json = CredentialPorter.Export(new[] { SqlAuthServer("s", "p") }, "right-passphrase");
            Assert.ThrowsAny<CryptographicException>(
                () => CredentialPorter.Import(json, "wrong-passphrase"));
        }

        [Fact]
        public void Import_CrossPassphrase_IsolatedFromOtherBundles()
        {
            var bundleA = CredentialPorter.Export(new[] { SqlAuthServer("a", "p") }, "alpha");
            var bundleB = CredentialPorter.Export(new[] { SqlAuthServer("b", "p") }, "bravo");

            // A's passphrase shouldn't open B
            Assert.ThrowsAny<CryptographicException>(() => CredentialPorter.Import(bundleA, "bravo"));
            Assert.ThrowsAny<CryptographicException>(() => CredentialPorter.Import(bundleB, "alpha"));

            // Each opens with its own passphrase
            Assert.Single(CredentialPorter.Import(bundleA, "alpha"));
            Assert.Single(CredentialPorter.Import(bundleB, "bravo"));
        }

        [Fact]
        public void Import_TamperedCiphertext_ThrowsCryptographicException()
        {
            var json = CredentialPorter.Export(new[] { SqlAuthServer("s", "secret") }, "ph");
            var tampered = TamperEncPassword(json, byteOffset: 30, flipMask: 0x01);

            // AES-GCM tag check must fail
            Assert.ThrowsAny<CryptographicException>(() => CredentialPorter.Import(tampered, "ph"));
        }

        [Fact]
        public void Import_TamperedTag_ThrowsCryptographicException()
        {
            // Flip a byte inside the 16-byte tag region (offset 12..27 after nonce(12))
            var json = CredentialPorter.Export(new[] { SqlAuthServer("s", "secret") }, "ph");
            var tampered = TamperEncPassword(json, byteOffset: 15, flipMask: 0xFF);
            Assert.ThrowsAny<CryptographicException>(() => CredentialPorter.Import(tampered, "ph"));
        }

        [Fact]
        public void Import_TamperedSalt_ThrowsCryptographicException()
        {
            // Flipping a byte in the salt produces a different derived key → decryption fails.
            var json = CredentialPorter.Export(new[] { SqlAuthServer("s", "p") }, "ph");
            var tampered = MutateField(json, "salt", b => { b[0] ^= 0xFF; return b; });
            Assert.ThrowsAny<CryptographicException>(() => CredentialPorter.Import(tampered, "ph"));
        }

        [Fact]
        public void Import_CorruptBase64EncPassword_ThrowsCryptographicException()
        {
            var json = CredentialPorter.Export(new[] { SqlAuthServer("s", "p") }, "ph");
            // Inject an illegal base64 char into the encPassword string
            var corrupt = json.Replace("\"encPassword\": \"", "\"encPassword\": \"!!!", StringComparison.Ordinal);
            Assert.ThrowsAny<CryptographicException>(() => CredentialPorter.Import(corrupt, "ph"));
        }

        [Fact]
        public void Import_TruncatedCiphertext_Throws()
        {
            var json = CredentialPorter.Export(new[] { SqlAuthServer("s", "p") }, "ph");
            var truncated = MutateField(json, "encPassword", b =>
            {
                // Drop the last byte of the AES-GCM blob → tag misalignment
                Array.Resize(ref b, Math.Max(b.Length - 1, 0));
                return b;
            });
            Assert.ThrowsAny<Exception>(() => CredentialPorter.Import(truncated, "ph"));
        }

        // Input validation -----------------------------------------------

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Export_EmptyOrNullPassphrase_ThrowsArgumentException(string? passphrase)
        {
            Assert.Throws<ArgumentException>(
                () => CredentialPorter.Export(new[] { SqlAuthServer("s", "p") }, passphrase!));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Import_EmptyOrNullPassphrase_ThrowsArgumentException(string? passphrase)
        {
            var json = CredentialPorter.Export(new[] { SqlAuthServer("s", "p") }, "ph");
            Assert.Throws<ArgumentException>(() => CredentialPorter.Import(json, passphrase!));
        }

        [Fact]
        public void Import_MalformedJson_ThrowsFormatException()
        {
            Assert.Throws<FormatException>(() => CredentialPorter.Import("{ not json", "ph"));
        }

        [Fact]
        public void Import_MissingSalt_ThrowsFormatException()
        {
            // Build a bundle with no salt field
            var json = """
            {
              "exportedAt": "2026-05-15T00:00:00Z",
              "exportedFrom": "test",
              "servers": []
            }
            """;
            Assert.Throws<FormatException>(() => CredentialPorter.Import(json, "ph"));
        }

        [Fact]
        public void Import_NullServers_ReturnsEmptyList()
        {
            // Salt present, servers null → import should tolerate (no encrypted payload to decrypt)
            var json = """
            {
              "exportedAt": "2026-05-15T00:00:00Z",
              "salt": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "servers": null
            }
            """;
            var imported = CredentialPorter.Import(json, "ph");
            Assert.Empty(imported);
        }

        [Fact]
        public void Import_AssignsNewIdWhenMissing()
        {
            // Hand-build a bundle entry with no id; salt + valid (but unused) encryption fields.
            // No SQL-auth entries means no decryption attempted.
            var json = """
            {
              "exportedAt": "2026-05-15T00:00:00Z",
              "salt": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "servers": [
                { "serverNames": "no-id-srv", "useWindowsAuth": true }
              ]
            }
            """;
            var imported = CredentialPorter.Import(json, "ph");
            Assert.Single(imported);
            Assert.False(string.IsNullOrEmpty(imported[0].Id));
            Assert.True(Guid.TryParse(imported[0].Id, out _));
        }

        [Fact]
        public void Import_DefaultsConnectionTimeoutTo15WhenZero()
        {
            var json = """
            {
              "exportedAt": "2026-05-15T00:00:00Z",
              "salt": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "servers": [
                { "serverNames": "s", "useWindowsAuth": true, "connectionTimeout": 0 }
              ]
            }
            """;
            Assert.Equal(15, CredentialPorter.Import(json, "ph")[0].ConnectionTimeout);
        }

        [Fact]
        public void Import_DefaultsDatabaseToMasterWhenMissing()
        {
            var json = """
            {
              "exportedAt": "2026-05-15T00:00:00Z",
              "salt": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "servers": [
                { "serverNames": "s", "useWindowsAuth": true }
              ]
            }
            """;
            Assert.Equal("master", CredentialPorter.Import(json, "ph")[0].Database);
        }

        // PBKDF2 cost ----------------------------------------------------

        [Fact]
        public void Pbkdf2Iters_AtLeastOwasp2023Minimum()
        {
            // Doctrine #3: security gating. Lock the PBKDF2 iteration count to >= the
            // OWASP 2023 minimum (310k for HMAC-SHA256). Reflection because the constant is
            // private — but it's a load-bearing security parameter that must not regress silently.
            var field = typeof(CredentialPorter).GetField(
                "Pbkdf2Iters",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(field);
            var iters = (int)field!.GetRawConstantValue()!;
            Assert.True(iters >= 310_000,
                $"Pbkdf2Iters is {iters} — below OWASP 2023 minimum of 310,000 for PBKDF2-HMAC-SHA256.");
        }

        [Fact]
        public void SaltBytes_AtLeast16()
        {
            // NIST SP 800-132 recommends >= 128-bit salt for PBKDF2.
            var field = typeof(CredentialPorter).GetField(
                "SaltBytes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(field);
            var bytes = (int)field!.GetRawConstantValue()!;
            Assert.True(bytes >= 16, $"SaltBytes is {bytes} — below NIST SP 800-132 minimum of 16.");
        }

        [Fact]
        public void KeyBytes_Is32_ForAes256()
        {
            var field = typeof(CredentialPorter).GetField(
                "KeyBytes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(field);
            Assert.Equal(32, (int)field!.GetRawConstantValue()!);
        }

        // Helpers --------------------------------------------------------

        private static string? ParseField(string json, string field)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty(field).GetString();
        }

        /// <summary>
        /// Reads servers[0].encPassword (base64), mutates the byte at <paramref name="byteOffset"/>
        /// by XOR with <paramref name="flipMask"/>, re-encodes, and rewrites the JSON.
        /// </summary>
        private static string TamperEncPassword(string json, int byteOffset, byte flipMask)
        {
            return MutateField(json, "encPassword", bytes =>
            {
                if (byteOffset < bytes.Length) bytes[byteOffset] ^= flipMask;
                return bytes;
            });
        }

        /// <summary>
        /// Top-level field mutation (e.g. "salt") OR per-server field mutation (e.g. "encPassword").
        /// Decodes the base64 value, lets the caller mutate the bytes, re-encodes, returns new JSON.
        /// </summary>
        private static string MutateField(string json, string field, Func<byte[], byte[]> mutator)
        {
            using var doc = JsonDocument.Parse(json);
            var node = JsonNode.Parse(json)!;

            string? b64;
            if (field == "salt")
            {
                b64 = node["salt"]!.GetValue<string>();
                var bytes = Convert.FromBase64String(b64);
                bytes = mutator(bytes);
                node["salt"] = Convert.ToBase64String(bytes);
            }
            else
            {
                // assume per-server
                var arr = (JsonArray)node["servers"]!;
                var first = arr[0]!;
                b64 = first[field]!.GetValue<string>();
                var bytes = Convert.FromBase64String(b64);
                bytes = mutator(bytes);
                first[field] = Convert.ToBase64String(bytes);
            }

            return node.ToJsonString();
        }
    }
}
