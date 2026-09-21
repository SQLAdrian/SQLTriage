/* In the name of God, the Merciful, the Compassionate */

using System;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Pins WHICH native SQLite provider this process actually initialises.
    ///
    /// <para>There is no explicit <c>Batteries_V2.Init()</c> or <c>SetProvider</c> call anywhere
    /// in this tree, so the provider is chosen entirely by which <c>SQLitePCLRaw.batteries_v2.dll</c>
    /// wins the output-directory filename collision. That is not a theoretical fragility: on
    /// 2026-08-12 a floated lock put <c>SQLitePCLRaw.core</c> 2.1.12 against
    /// <c>provider.e_sqlcipher</c> 2.1.10, the plain <c>e_sqlite3</c> bundle won the registration,
    /// and every store this application opened came back Plaintext with no error anywhere
    /// (CI run 31580506025). The rationale block at the SQLite PackageReference in
    /// <c>SQLTriage.csproj</c> carries the whole account.</para>
    ///
    /// <para>Since 2026-09-02 (lane/nu1903-sqlite-core) the reference is
    /// <c>Microsoft.Data.Sqlite.Core</c> rather than the <c>Microsoft.Data.Sqlite</c> metapackage,
    /// so <c>SQLitePCLRaw.bundle_e_sqlite3</c> - and with it the plain provider and its
    /// <c>batteries_v2</c> - is no longer in the graph at all. That removes the landing pad, and
    /// this test is what proves it stayed removed: it measures the provider instead of inferring
    /// it from the dependency graph.</para>
    ///
    /// <para>The measurement is <c>PRAGMA cipher_version</c>, which returns a row ONLY under
    /// SQLCipher. A plain <c>e_sqlite3</c> build parses the pragma and returns nothing, so a
    /// missing row is the exact signature of the failure this test exists to catch.</para>
    /// </summary>
    public class SqliteProviderIdentityTests
    {
        private const string NoRow = "NOROW_NOT_SQLCIPHER";

        private readonly ITestOutputHelper _out;

        public SqliteProviderIdentityTests(ITestOutputHelper output) { _out = output; }

        [Fact]
        public void The_provider_this_process_initialises_is_SQLCipher_never_the_plain_SQLite3_build()
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();

            var cipherVersion = NoRow;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA cipher_version;";
                var value = cmd.ExecuteScalar();
                if (value != null && value != DBNull.Value)
                {
                    cipherVersion = Convert.ToString(value) ?? "(null)";
                }
            }

            string sqliteVersion;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "select sqlite_version();";
                sqliteVersion = Convert.ToString(cmd.ExecuteScalar()) ?? "(null)";
            }

            // Reported, never asserted. The exact versions are a property of the pinned native
            // and are recorded in Data/SqliteCipherHelper.cs and the vendor dependency register;
            // asserting them here would turn a deliberate, reviewed native bump into a failure
            // in a test about provider IDENTITY. Printing them keeps the measurement in the
            // test log, where a reader chasing a cipher question can find it.
            _out.WriteLine("PRAGMA cipher_version = " + cipherVersion);
            _out.WriteLine("sqlite_version()      = " + sqliteVersion);

            Assert.True(
                cipherVersion != NoRow,
                "PRAGMA cipher_version returned no row, which means the native provider "
                + "SQLitePCLRaw initialised is NOT SQLCipher. That is the 2026-08-12 shape: the "
                + "plain e_sqlite3 build won the batteries_v2 registration and every store this "
                + "application opens would be written as plaintext, silently. Check that "
                + "SQLTriage.csproj still references Microsoft.Data.Sqlite.Core (never the "
                + "Microsoft.Data.Sqlite metapackage, which drags bundle_e_sqlite3 back in) and "
                + "that packages.lock.json has no SQLitePCLRaw.bundle_e_sqlite3 entry.");

            Assert.False(
                string.IsNullOrWhiteSpace(sqliteVersion),
                "sqlite_version() came back empty, so the measurement above cannot be trusted "
                + "either. Fail loudly rather than report a provider identity read off a "
                + "connection that is not answering.");
        }
    }
}
