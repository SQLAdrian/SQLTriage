/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

#pragma warning disable CA1416 // Windows-only API — project targets net8.0-windows
namespace SQLTriage.Data
{
    /// <summary>
    /// Manages the SQLCipher master key and provides a single helper for opening
    /// encrypted SQLite connections.
    ///
    /// Key lifecycle:
    ///   - On first use: generates 32 random bytes, DPAPI-wraps them (LocalMachine scope),
    ///     writes to Config/.sqlite-cipher-key (hidden). Same pattern as CredentialProtector.
    ///   - On subsequent uses: reads + unwraps the key file.
    ///
    /// Migration: if the target DB file exists but is unencrypted (detected by running a
    /// probe query after applying the key), the plain file is deleted and rebuilt from
    /// scratch. All SQLite stores are regenerable caches — no master data is lost.
    /// </summary>
    public static class SqliteCipherHelper
    {
        private static readonly byte[] AppEntropy =
            System.Text.Encoding.UTF8.GetBytes("SQLTriage.SqliteCipher.v1");

        private static readonly string KeyFilePath =
            Path.Combine(AppContext.BaseDirectory, "config", ".sqlite-cipher-key");

        // Cached hex key (computed once per process)
        private static string? _hexKey;
        private static readonly object _keyLock = new();

        // ──────────────────────────────────────────────────────────────────
        //  Public API
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Opens and returns an encrypted SqliteConnection for the given path.
        /// Applies PRAGMA key immediately after open. Detects and handles plain
        /// (unencrypted) existing files by deleting them so they regenerate.
        /// Caller must dispose the returned connection.
        /// </summary>
        public static SqliteConnection OpenEncrypted(string connectionString)
        {
            var hexKey = GetOrCreateHexKey();
            var conn = new SqliteConnection(connectionString);
            conn.Open();

            using var keyCmd = conn.CreateCommand();
            keyCmd.CommandText = $"PRAGMA key = \"x'{hexKey}'\";";
            keyCmd.ExecuteNonQuery();

            if (!IsKeyValid(conn, connectionString))
                conn = ReopenAfterMigration(connectionString, hexKey);

            return conn;
        }

        /// <summary>
        /// Async variant of <see cref="OpenEncrypted"/>.
        /// </summary>
        public static async Task<SqliteConnection> OpenEncryptedAsync(string connectionString)
        {
            var hexKey = GetOrCreateHexKey();
            var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            using var keyCmd = conn.CreateCommand();
            keyCmd.CommandText = $"PRAGMA key = \"x'{hexKey}'\";";
            await keyCmd.ExecuteNonQueryAsync();

            if (!IsKeyValid(conn, connectionString))
                conn = await ReopenAfterMigrationAsync(connectionString, hexKey);

            return conn;
        }

        // ──────────────────────────────────────────────────────────────────
        //  Key management
        // ──────────────────────────────────────────────────────────────────

        private static string GetOrCreateHexKey()
        {
            if (_hexKey != null) return _hexKey;

            lock (_keyLock)
            {
                if (_hexKey != null) return _hexKey;
                _hexKey = LoadOrGenerateHexKey();
            }

            return _hexKey;
        }

        private static string LoadOrGenerateHexKey()
        {
            byte[] rawKey;

            if (File.Exists(KeyFilePath))
            {
                try
                {
                    var protected_ = File.ReadAllBytes(KeyFilePath);
                    rawKey = ProtectedData.Unprotect(protected_, AppEntropy, DataProtectionScope.LocalMachine);
                    if (rawKey.Length == 32)
                        return Convert.ToHexString(rawKey);

                    // Wrong length — regenerate
                    Serilog.Log.Warning("[SqliteCipherHelper] SQLite cipher key file has unexpected length {Len}; regenerating", rawKey.Length);
                }
                catch (CryptographicException ex)
                {
                    Serilog.Log.Warning(ex, "[SqliteCipherHelper] Could not unwrap SQLite cipher key (machine change?); regenerating");
                }
            }

            // Generate new key
            rawKey = new byte[32];
            RandomNumberGenerator.Fill(rawKey);

            var protectedBytes = ProtectedData.Protect(rawKey, AppEntropy, DataProtectionScope.LocalMachine);

            var dir = Path.GetDirectoryName(KeyFilePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(KeyFilePath, protectedBytes);

            try { new FileInfo(KeyFilePath).Attributes |= FileAttributes.Hidden; }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[SqliteCipherHelper] Failed to set hidden attribute on cipher key file"); }

            Serilog.Log.Information("[SqliteCipherHelper] Generated new SQLite cipher key at {Path}", KeyFilePath);
            return Convert.ToHexString(rawKey);
        }

        // ──────────────────────────────────────────────────────────────────
        //  Migration helpers
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the connection can execute a trivial probe query.
        /// A failure means the file is unencrypted (or a different key was used).
        /// </summary>
        private static bool IsKeyValid(SqliteConnection conn, string connectionString)
        {
            try
            {
                // Read probe (cheap, works on existing keyed files).
                using (var probe = conn.CreateCommand())
                {
                    probe.CommandText = "SELECT name FROM sqlite_master LIMIT 1;";
                    probe.ExecuteScalar();
                }
                // Encryption-commit probe (2026-05-21 fix): a fresh SQLCipher
                // file created by conn.Open() before PRAGMA key materialises
                // an UNencrypted header; the read above succeeds against it,
                // but subsequent schema/write ops (e.g. PRAGMA journal_mode=WAL)
                // throw "malformed database schema". Force a schema-write here
                // so a still-unencrypted file fails NOW (caught + recovered)
                // rather than later (uncaught + dispatcher exception).
                using (var commit = conn.CreateCommand())
                {
                    commit.CommandText = "CREATE TABLE IF NOT EXISTS _sqlcipher_init (id INTEGER);";
                    commit.ExecuteNonQuery();
                }
                return true;
            }
            catch (SqliteException ex)
            {
                Serilog.Log.Warning(ex, "[SqliteCipherHelper] Probe failed for {ConnStr} — assuming plain (unencrypted) file; deleting for clean re-init", connectionString);
                return false;
            }
        }

        private static SqliteConnection ReopenAfterMigration(string connectionString, string hexKey)
        {
            DeleteDbFile(connectionString);
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            using (var keyCmd = conn.CreateCommand())
            {
                keyCmd.CommandText = $"PRAGMA key = \"x'{hexKey}'\";";
                keyCmd.ExecuteNonQuery();
            }
            CommitEncryption(conn);
            return conn;
        }

        private static async Task<SqliteConnection> ReopenAfterMigrationAsync(string connectionString, string hexKey)
        {
            DeleteDbFile(connectionString);
            var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();
            using (var keyCmd = conn.CreateCommand())
            {
                keyCmd.CommandText = $"PRAGMA key = \"x'{hexKey}'\";";
                await keyCmd.ExecuteNonQueryAsync();
            }
            CommitEncryption(conn);
            return conn;
        }

        /// <summary>
        /// Forces SQLCipher to materialise the encrypted header on a
        /// fresh-from-delete file. Must run AFTER PRAGMA key, BEFORE any
        /// other schema/write op. Without this the file stays in a
        /// transient state where reads work but the first schema write
        /// throws "malformed database schema" — see <see cref="IsKeyValid"/>.
        /// </summary>
        private static void CommitEncryption(SqliteConnection conn)
        {
            using var commit = conn.CreateCommand();
            commit.CommandText = "CREATE TABLE IF NOT EXISTS _sqlcipher_init (id INTEGER);";
            commit.ExecuteNonQuery();
        }

        /// <summary>
        /// Deletes the .db file and its WAL/SHM siblings. Best-effort.
        /// </summary>
        private static void DeleteDbFile(string connectionString)
        {
            try
            {
                // Extract Data Source from connection string
                var builder = new SqliteConnectionStringBuilder(connectionString);
                var path = builder.DataSource;
                if (string.IsNullOrEmpty(path) || path == ":memory:") return;

                SqliteConnection.ClearAllPools();

                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var candidate = path + suffix;
                    if (File.Exists(candidate))
                    {
                        try { File.Delete(candidate); }
                        catch (IOException) { /* file locked by previous process — will be cleaned on next restart */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[SqliteCipherHelper] Failed to delete plain SQLite file during migration");
            }
        }
    }
}
#pragma warning restore CA1416
