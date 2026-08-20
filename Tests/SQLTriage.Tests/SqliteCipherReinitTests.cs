/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The wrong-key re-init path, driven against real files rather than reasoned about.
    ///
    /// <para>The defect: DeleteDbFile was a void described as "best-effort", and
    /// ReopenAfterMigration opened at the same path whether or not the old file had gone. When the
    /// move and the delete both failed, SQLCipher's CommitEncryption threw "malformed database
    /// schema" — an error about SQL schema, for a problem that was a locked file — out of whichever
    /// of the 25 OpenEncrypted consumers happened to be constructing. A store constructed at
    /// startup turns that into a Windows-SCM restart loop with no diagnosable cause.</para>
    ///
    /// <para>These tests hold the file open from a second handle, in the share mode a real
    /// second process uses, and assert what the caller is told.</para>
    ///
    /// <para><b>What the positive control found, which reading could not.</b> The re-init never
    /// removed the file at all — <c>OpenEncrypted</c> still had its OWN connection open on it, and
    /// Windows refuses to move or delete a file with a live handle. Every install has been taking
    /// the swallowed-failure branch, so the H2 "preserve the unreadable file aside" defence had
    /// never once run. The verify turned a silent no-op into a visible failure, and the failure
    /// named its own cause.</para>
    ///
    /// <para><b>2026-08-06 — and the positive control's assertion INVERTED.</b> It used to assert
    /// <c>Assert.NotEmpty(...pre-reinit-*)</c>, which pinned the aside into existence. Once the
    /// rename actually started running, that aside turned out to be, on the common
    /// plain→encrypted path, a byte-for-byte copy of the UNENCRYPTED store, kept for ever, one
    /// more per re-init. Adrian's ruling (Option B) splits the population in two and the tests
    /// split with it:</para>
    /// <list type="bullet">
    ///   <item><description>PLAINTEXT copies — readable by anyone with the folder or a backup of
    ///   it, which is precisely the threat DPAPI-LocalMachine encryption exists to stop — are
    ///   deleted, but only after the replacement store is PROVEN to open and read back.</description></item>
    ///   <item><description>UNREADABLE copies — encrypted under a key nobody has, or corrupt — leak
    ///   nothing and are the only surviving copy of that data. Kept, unchanged.</description></item>
    /// </list>
    /// <para>So the assertions below are about which copies survive and which do not, and the
    /// ORDERING that decides it: a re-init that fails after the rename must leave everything.</para>
    ///
    /// <para><b>2026-08-06, the adversarial round, and what it changed about the SHAPE of this
    /// file.</b> Seventeen tests passed while three mutations removed substance from the ruling and
    /// nobody noticed: the delete moved ahead of the read-back (with a forged proof value), the
    /// delete-failure handler made to rethrow, and the line that closes the story on the failure
    /// path deleted. Two causes, both structural rather than careless.</para>
    /// <list type="number">
    ///   <item><description>Everything was driven END TO END, and the end-to-end path cannot reach
    ///   these. Every way of breaking a re-init from outside the process breaks it at Open or at
    ///   CommitEncryption, both earlier than the read-back; and nothing outside can take a handle on
    ///   the copy between the File.Move that creates it and the delete. So the helper's internals
    ///   are now driven directly as well, with the end-to-end tests kept as the controls that prove
    ///   the direct ones are describing the real path.</description></item>
    ///   <item><description>NO LOG OUTPUT WAS ASSERTED ANYWHERE — zero Serilog references in the
    ///   whole file. On this path the log IS the deliverable: points 5 and 7 of the ruling are
    ///   entirely about what an operator is told about data that has just been destroyed. Lines are
    ///   now captured and asserted, including the one that says the replacement store came back
    ///   EMPTY, which is the whole consequence of the operation for check-baselines.db.</description></item>
    /// </list>
    /// </summary>
    public class SqliteCipherReinitTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "sqlt-cipher-reinit-" + Guid.NewGuid().ToString("N"));

        /// <summary>The 16 ASCII bytes every plain, unencrypted SQLite database file opens with.</summary>
        private static readonly byte[] PlainHeader =
            System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");

        /// <summary>
        /// A 32-byte key this process is NOT the owner of, used to manufacture the second
        /// population honestly (see <see cref="CreateForeignKeyedDatabase"/>).
        /// </summary>
        private const string ForeignHexKey =
            "0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0";

        public SqliteCipherReinitTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { SqliteConnection.ClearAllPools(); } catch { /* teardown */ }
            try { Directory.Delete(_dir, recursive: true); } catch { /* teardown */ }
        }

        /// <summary>A PLAIN, unencrypted SQLite file — the input that triggers the re-init path.</summary>
        private string CreatePlainDatabase(string name)
        {
            var path = Path.Combine(_dir, name);
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE canary (id INTEGER); INSERT INTO canary VALUES (1);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            Assert.True(File.Exists(path));
            // The input has to BE the thing it is named after, or every assertion downstream is
            // about a file the test only believes is plain.
            Assert.Equal(PlainHeader, Head(path, PlainHeader.Length));
            return path;
        }

        /// <summary>
        /// A REAL SQLCipher database encrypted under a key this process does not hold — the second
        /// population, the one that must be preserved.
        ///
        /// <para>Not faked, and not a corrupted file dressed up as one. The honest alternative
        /// would have been to reproduce the production cause (a DPAPI unwrap fault makes
        /// LoadOrGenerateHexKey regenerate the key, orphaning every store), and that is not
        /// reachable from a test: the hex key is cached in a process-wide static and the key file
        /// path is fixed at AppContext.BaseDirectory. So the state is reached from the other end —
        /// a genuine SQLCipher file is written here under a different key, which is byte-for-byte
        /// the same situation the operator's disk is in after a key regeneration.</para>
        /// </summary>
        private string CreateForeignKeyedDatabase(string name)
        {
            var path = Path.Combine(_dir, name);
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using (var key = conn.CreateCommand())
                {
                    key.CommandText = $"PRAGMA key = \"x'{ForeignHexKey}'\";";
                    key.ExecuteNonQuery();
                }
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE secret (id INTEGER); INSERT INTO secret VALUES (42);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            Assert.True(File.Exists(path));
            // If the provider had quietly ignored PRAGMA key this would be a plain file, and every
            // test built on it would be measuring the wrong population without saying so.
            Assert.NotEqual(PlainHeader, Head(path, PlainHeader.Length));
            return path;
        }

        private static byte[] Head(string path, int count)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var got = stream.Read(buffer, read, count - read);
                if (got == 0) break;
                read += got;
            }
            return buffer.Take(read).ToArray();
        }

        /// <summary>
        /// The MAIN pre-re-init copies for a store — not their -wal/-shm/-journal siblings.
        ///
        /// <para>The filter arrived with the sidecar move (2026-08-08) and it is not cosmetic:
        /// before it, <c>Assert.Single(Asides(...))</c> read as "one copy was left" while actually
        /// asserting "exactly one FILE matched", so a store with a WAL would have failed a test
        /// about renaming for a reason that had nothing to do with renaming.</para>
        /// </summary>
        private string[] Asides(string dbName) =>
            Directory.GetFiles(_dir, dbName + ".pre-reinit-*")
                .Where(p => !SidecarSuffixes.Any(s => p.EndsWith(s, StringComparison.Ordinal)))
                .ToArray();

        /// <summary>The sibling files that travelled with a pre-re-init copy.</summary>
        private string[] AsideSidecars(string dbName) =>
            Directory.GetFiles(_dir, dbName + ".pre-reinit-*")
                .Where(p => SidecarSuffixes.Any(s => p.EndsWith(s, StringComparison.Ordinal)))
                .ToArray();

        private static readonly string[] SidecarSuffixes = { "-wal", "-shm", "-journal" };

        // ──────────────────────────────────────────────────────────────────
        //  The plaintext population: deleted, but only once the store is proven
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void POSITIVE_CONTROL_a_plain_file_that_reinitialises_leaves_no_unencrypted_copy()
        {
            // Without this, the refusal tests below could pass because the path never runs at all.
            var path = CreatePlainDatabase("clearable.db");

            using (var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={path}"))
            {
                Assert.Equal(System.Data.ConnectionState.Open, conn.State);
            }

            // THE INVERTED ASSERTION (2026-08-06). This used to be Assert.NotEmpty. The copy was a
            // readable database, and leaving it beside the encrypted store defeated the one thing
            // the encryption actually buys: that a stolen folder, backup or support bundle cannot
            // be read off-box, because DPAPI LocalMachine will not unwrap the key there.
            Assert.Empty(Asides("clearable.db"));

            // And what replaced it is a real encrypted store, not merely a file at the right path.
            Assert.True(File.Exists(path));
            Assert.NotEqual(PlainHeader, Head(path, PlainHeader.Length));

            using (var reopened = SqliteCipherHelper.OpenEncrypted($"Data Source={path}"))
            {
                using var cmd = reopened.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = '_sqlcipher_init';";
                Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task The_async_path_also_removes_the_unencrypted_copy()
        {
            // Two entry points, one ruling. A fix applied to only one of them is the shape this
            // tree has shipped before — and the deletion is the half that leaves evidence on disk.
            var path = CreatePlainDatabase("clearable-async.db");

            using (var conn = await SqliteCipherHelper.OpenEncryptedAsync($"Data Source={path}"))
            {
                Assert.Equal(System.Data.ConnectionState.Open, conn.State);
            }

            Assert.Empty(Asides("clearable-async.db"));
            Assert.True(File.Exists(path));
            Assert.NotEqual(PlainHeader, Head(path, PlainHeader.Length));
        }

        // ──────────────────────────────────────────────────────────────────
        //  The unreadable population: kept, byte for byte
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void A_store_encrypted_under_a_key_we_do_not_have_is_preserved_aside()
        {
            // Nobody can read this file — not an attacker, not Adrian. Deleting it protects no one
            // and destroys the only copy of whatever was in it.
            //
            // ⚠ 2026-08-08: the store name is now load-bearing. uptime.db is on the EMPTY-OK half
            // of the fallback registry, so this is the population that still re-initialises empty
            // when its contents cannot be carried across. The same file under a REFUSE name does
            // something else entirely — see the two tests below this one.
            var path = CreateForeignKeyedDatabase("uptime.db");
            var original = File.ReadAllBytes(path);

            using (var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={path}"))
            {
                Assert.Equal(System.Data.ConnectionState.Open, conn.State);
            }

            var aside = Assert.Single(Asides("uptime.db"));
            // Preserved, and preserved INTACT — a rename that quietly truncated would still leave
            // a file at the path and still pass a mere existence check.
            Assert.Equal(original, File.ReadAllBytes(aside));

            // The fresh store took its place and is itself encrypted.
            Assert.True(File.Exists(path));
            Assert.NotEqual(PlainHeader, Head(path, PlainHeader.Length));
        }

        // ──────────────────────────────────────────────────────────────────
        //  The fallback registry (Adrian's ruling, 2026-08-08, point 3)
        //
        //  A store whose contents cannot be carried across gets one of two answers, and which one
        //  is a property of WHAT THE STORE HOLDS. The registry is the ruling; a registry nothing
        //  asserts against is a comment.
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void A_refuse_store_that_cannot_be_read_is_not_replaced_with_an_empty_one()
        {
            // check-baselines.db holds F6 accepted findings a DBA ticked off one at a time. Under a
            // key nobody has it cannot be exported, and the ruling's answer is to refuse rather
            // than hand back an empty database that looks like a working one.
            var path = CreateForeignKeyedDatabase("check-baselines.db");
            var original = File.ReadAllBytes(path);

            var ex = Assert.ThrowsAny<Exception>(() => SqliteCipherHelper.OpenEncrypted($"Data Source={path}"));

            Assert.Contains("will not re-initialise", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("check-baselines.db", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Not renamed, not replaced — the operator's file is where it has always been, a stronger
            // promise than an aside. The MESSAGE claims only what this run did ("THIS RUN HAS NOT
            // RENAMED, MOVED, COPIED OR DELETED IT"), not that the bytes are untouched: the key probe
            // itself can checkpoint a hot WAL (probe (j), 2026-08-08). The byte-equality below holds
            // HERE because this fixture has no hot WAL — it is this test's setup, not a code promise.
            Assert.Empty(Asides("check-baselines.db"));
            Assert.Equal(original, File.ReadAllBytes(path));
        }

        [Fact]
        public void An_unlisted_store_defaults_to_refusing()
        {
            // THE FAIL-CLOSED DEFAULT. A store nobody has classified is a store nobody has
            // established is a cache. Without this, adding a fifteenth store to this application
            // silently opts it into being emptied.
            var path = CreateForeignKeyedDatabase("nobody-classified-this.db");
            var original = File.ReadAllBytes(path);

            Assert.Equal(
                SqliteCipherHelper.ReinitFallback.Refuse,
                SqliteCipherHelper.FallbackPolicyFor(path));

            Assert.ThrowsAny<Exception>(() => SqliteCipherHelper.OpenEncrypted($"Data Source={path}"));

            Assert.Empty(Asides("nobody-classified-this.db"));
            Assert.Equal(original, File.ReadAllBytes(path));
        }

        [Theory]
        // ⚠ The expected policy is a BOOL, not the ReinitFallback enum: the enum is internal, and
        // an xunit [Theory] method has to be public, which makes an internal parameter type a
        // compile error (CS0051). "Refuses" is the question either way.
        [InlineData("check-baselines.db", true)]
        [InlineData("server-config-baselines.db", true)]
        [InlineData("change-items.db", true)]
        [InlineData("alert-history.db", true)]
        [InlineData("seat-register.db", true)]
        [InlineData("governance-history.db", false)]
        [InlineData("changed-objects.db", false)]
        [InlineData("blocking-history.db", false)]
        [InlineData("SQLTriage.db", false)]
        [InlineData("code-hotspots-cache.db", false)]
        [InlineData("scheduled-task-history.db", false)]
        [InlineData("consolidation-history.db", false)]
        [InlineData("uptime.db", false)]
        [InlineData("SQLTriage-cache.db", false)]
        public void Each_ruled_store_gets_the_policy_the_ruling_gave_it(string storeName, bool refuses)
        {
            // ⚠ THIS TEST DOES NOT ENUMERATE ANYTHING, and until 2026-08-08 its name said it did:
            // "Every_store_this_application_opens_has_a_ruled_fallback", over a hard-coded
            // [InlineData] list. A fifteenth store added to the application failed nothing here —
            // the only direction the list catches is a store being dropped OUT of the registry,
            // because then its InlineData row starts refusing. The direction that reaches a client
            // is the other one, and it is now measured against the SOURCE TREE by
            // Every_store_this_application_opens_is_in_the_fallback_registry below.
            //
            // What this one is for, and it is worth keeping: each verdict in the registry is a
            // separate assertion about what a store holds, and a single-line edit that flips one
            // of them is caught here per store rather than in aggregate.
            var expected = refuses
                ? SqliteCipherHelper.ReinitFallback.Refuse
                : SqliteCipherHelper.ReinitFallback.EmptyOk;

            Assert.Equal(expected, SqliteCipherHelper.FallbackPolicyFor(
                Path.Combine(_dir, "some", "install", "path", storeName)));

            // Keyed on the NAME, not the folder: three of these live under Data/ and the rest do
            // not, and the same store moves between them across versions.
            Assert.Equal(expected, SqliteCipherHelper.FallbackPolicyFor(storeName));
        }

        /// <summary>The store names deliberately outside the registry, each with the measurement that lets them out.</summary>
        private static readonly string[] NotAStoreThisHelperOpens = { "rag.db" };

        [Fact]
        public void Every_store_this_application_opens_is_in_the_fallback_registry()
        {
            // THE CENSUS, driven from the source tree rather than from a second hand-written list.
            // A registry checked against a list someone wrote at the same time as the registry is a
            // registry checked against itself: the theory above did exactly that, and a fifteenth
            // store wired into the app would have inherited the refusing default silently and first
            // announced itself as an IOException at startup on a client box.
            //
            // The scan is deliberately wider than "files that call OpenEncrypted": ANY .db literal
            // in the shipped tree has to be either ruled on or explicitly excluded with a reason.
            // Fail-closed matches the registry's own default.
            var root = RawPassedScan.RepoRoot();
            var literal = new System.Text.RegularExpressions.Regex(
                "\"(?<n>[A-Za-z0-9._-]+\\.db)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

            var namedIn = new System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.List<string>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var file in RawPassedScan.SourceFiles(root))
            {
                // The registry lives in this file and names all of them; scanning it would be the
                // self-comparison this test exists to replace.
                if (Path.GetFileName(file).Equals("SqliteCipherHelper.cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var text = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match m in literal.Matches(text))
                {
                    var name = m.Groups["n"].Value;
                    if (!namedIn.TryGetValue(name, out var files))
                        namedIn[name] = files = new System.Collections.Generic.List<string>();
                    if (!files.Contains(file, StringComparer.OrdinalIgnoreCase)) files.Add(file);
                }
            }

            // THE CONTROL. A scan that matched nothing would agree with an empty registry and call
            // it a pass, which is how a census becomes a decoration.
            Assert.True(
                namedIn.Count >= 10,
                $"The scan found only {namedIn.Count} .db literals under {string.Join(", ", RawPassedScan.ScanRoots)} — "
                + "it is not reading the tree it is supposed to be reading.");

            // THE EXCLUSION IS MEASURED, NOT ASSERTED. rag.db is a retrieval corpus a customer
            // drops next to the exe; RagDatabaseService only asks whether it EXISTS and how big it
            // is. That is a claim about the code, so it is checked here rather than taken on
            // trust: if any file naming it ever reaches for the cipher helper, this fails and the
            // store has to be ruled on.
            foreach (var excluded in NotAStoreThisHelperOpens)
            {
                Assert.True(namedIn.ContainsKey(excluded),
                    $"'{excluded}' is excluded from the registry census but no longer appears in the tree — "
                    + "delete the exclusion rather than leaving a rule for nothing.");

                foreach (var file in namedIn[excluded])
                {
                    Assert.DoesNotContain(
                        "SqliteCipherHelper", File.ReadAllText(file), StringComparison.Ordinal);
                }
            }

            var mustBeRuled = namedIn.Keys
                .Where(n => !NotAStoreThisHelperOpens.Contains(n, StringComparer.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var ruled = SqliteCipherHelper.RuledStoreNames
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Set equality, both directions: a store added to the app and never ruled on fails
            // here, and so does a registry entry for a store the app no longer opens.
            Assert.Equal(mustBeRuled, ruled, StringComparer.OrdinalIgnoreCase);
        }

        // ──────────────────────────────────────────────────────────────────
        //  The ordering: proof first, deletion second
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void A_reinit_that_fails_after_the_rename_leaves_the_copy_untouched()
        {
            // The entire substance of the ruling is the ORDER. If the deletion ever moves up to the
            // rename — or to "Open() returned without throwing" — then a re-init that cannot
            // rebuild the store has destroyed the operator's only copy of it and handed back
            // nothing. This test is the one that notices.
            //
            // The failure is injected by opening the store READ-ONLY: the plain file is still
            // readable enough for the probe to fail and the re-init to start, the rename still
            // succeeds (it is a filesystem operation, not a SQLite one), and then the reopen at the
            // now-vacant path cannot create the database because the connection may not write.
            // Every step after the rename fails; nothing may touch the copy.
            //
            // ⚠ 2026-08-08 — THE STORE NAME IS NOT THE INSTRUMENT, and the note that stood here
            // said it was, resting on a claim about this code's own behaviour that is false. It
            // read: "a read-only connection also cannot run the export — the destination is
            // ATTACHed through the same connection and inherits its read-only flag — so a REFUSE
            // store would throw before the rename". Measured on both halves: TryExportPlaintextStore
            // opens its OWN Mode=ReadWriteCreate connection to the source PATH and never uses the
            // caller's, which OpenEncrypted has already disposed by then. Run against
            // check-baselines.db (REFUSE) this same failure exported 3 schema objects and 5 rows,
            // REACHED the rename, and threw at the reopen — a SqliteException, not the policy
            // refusal. Probe (g)'s Error 14 is real, but it is about a source connection the export
            // does not use. The correction is held by an assertion rather than by this comment:
            // A_read_only_caller_still_exports_because_the_export_opens_its_own_connection.
            //
            // The name stays governance-history.db because a real store name exercises the real
            // registry lookup, and the ORDERING this test is about is the same on either half.
            var path = CreatePlainDatabase("governance-history.db");
            var original = File.ReadAllBytes(path);

            Assert.ThrowsAny<Exception>(
                () => SqliteCipherHelper.OpenEncrypted($"Data Source={path};Mode=ReadOnly"));

            var aside = Assert.Single(Asides("governance-history.db"));

            // It is the PLAINTEXT population — the deletable one — so its survival here is the
            // ordering being observed, not the discriminator declining to act.
            Assert.Equal(PlainHeader, Head(aside, PlainHeader.Length));
            Assert.Equal(original, File.ReadAllBytes(aside));
        }

        [Fact]
        public void A_read_only_caller_still_exports_because_the_export_opens_its_own_connection()
        {
            // THE ASSERTION THAT REPLACES A FALSE COMMENT (2026-08-08). Two tests above justified
            // their store names with "a read-only connection cannot run the export — the
            // destination is ATTACHed through the same connection and inherits its read-only flag
            // — so a REFUSE store would throw before the rename". Every clause of that is wrong,
            // and a claim about what this code guarantees is exactly the thing that gets proven or
            // not written. The mechanism: OpenEncrypted DISPOSES the caller's connection before the
            // re-init starts, and TryExportPlaintextStore opens its own Mode=ReadWriteCreate
            // connection to the source PATH. The caller's Mode never reaches the export.
            //
            // Driven on a REFUSE store on purpose: that is the half the old comment said could not
            // get here. If the export were ever rewired onto the caller's connection, this store
            // would fail its export, hit the refusing policy, and throw with nothing renamed — so
            // Asides() below would be empty and this test would fail. That is the mutation it holds.
            var path = CreateRichPlainDatabase("check-baselines.db");
            var original = File.ReadAllBytes(path);

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            Exception? thrown;
            try
            {
                thrown = Assert.ThrowsAny<Exception>(
                    () => SqliteCipherHelper.OpenEncrypted($"Data Source={path};Mode=ReadOnly"));
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            // (1) The export RAN and carried the contents — measured from its own line, which
            // prints the counts it verified rather than the fact that it was called.
            var all = captured.Snapshot(Serilog.Events.LogEventLevel.Verbose);
            Assert.Contains(all, m =>
                m.Contains("Exported", StringComparison.Ordinal)
                && m.Contains("6 schema object(s) and 50 row(s)", StringComparison.Ordinal));

            // (2) The rename HAPPENED, on a REFUSE store, and the copy is the plaintext original.
            var aside = Assert.Single(Asides("check-baselines.db"));
            Assert.Equal(original, File.ReadAllBytes(aside));

            // (3) And what threw was the read-only REOPEN, not the policy refusal — the refusal is
            // thrown before anything is renamed, so its text appearing here would mean (2) is
            // measuring some other run.
            Assert.DoesNotContain("will not re-initialise", thrown!.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_reinit_that_fails_at_the_encryption_commit_leaves_the_copy_untouched()
        {
            // The test above fails at Open, which leaves a deletion placed anywhere after Open
            // unexercised. This one gets FURTHER in: the connection opens, PRAGMA key succeeds, and
            // the failure lands on CommitEncryption — so a deletion moved to "we have a connection
            // and a key, that will do" is caught here rather than shipped.
            //
            // The instrument is a DIRECTORY occupying the rollback-journal name. CREATE TABLE needs
            // to write <store>.db-journal and cannot create a file over a directory, so it fails
            // with SQLite Error 14 at exactly the step CommitEncryption runs — verified, not
            // assumed: without the directory this same path completes and deletes the copy, which
            // is what the positive control asserts three tests up.
            var path = CreatePlainDatabase("fails-at-commit.db");
            var original = File.ReadAllBytes(path);
            Directory.CreateDirectory(path + "-journal");

            Assert.ThrowsAny<Exception>(() => SqliteCipherHelper.OpenEncrypted($"Data Source={path}"));

            var aside = Assert.Single(Asides("fails-at-commit.db"));
            Assert.Equal(PlainHeader, Head(aside, PlainHeader.Length));
            Assert.Equal(original, File.ReadAllBytes(aside));
        }

        // ──────────────────────────────────────────────────────────────────
        //  The refusal, unchanged in intent (pre-2026-08-06 tests)
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void A_file_that_cannot_be_cleared_refuses_by_name_instead_of_reopening()
        {
            var path = CreatePlainDatabase("held.db");

            // Held exactly as another process holding it for reading and writing would: SQLite
            // can still OPEN it (so the re-init path is reached at all), but Windows refuses a
            // move or a delete, because those need FILE_SHARE_DELETE and this share mode omits it.
            // FileShare.None was tried first and was the wrong instrument — it blocked the very
            // first conn.Open(), so the test failed with "SQLite Error 14" long before the code
            // under test ran, and would have passed a laxer assertion for the wrong reason.
            using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            var ex = Assert.ThrowsAny<Exception>(() => SqliteCipherHelper.OpenEncrypted($"Data Source={path}"));

            // The message names the file, so an operator reading a service log knows what to move.
            Assert.Contains(path, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("could not re-initialise", ex.Message, StringComparison.OrdinalIgnoreCase);

            // And it is NOT the schema error the old path produced, which described neither the
            // file nor the cause.
            Assert.DoesNotContain("malformed database schema", ex.Message, StringComparison.OrdinalIgnoreCase);

            // The held file is untouched: a refusal must not have destroyed what it refused over.
            Assert.True(File.Exists(path));
            Assert.Empty(Asides("held.db"));
        }

        [Fact]
        public async System.Threading.Tasks.Task The_async_path_refuses_the_same_way()
        {
            // Two entry points, one guard. A fix on only one of them is the shape this tree has
            // shipped before.
            var path = CreatePlainDatabase("held-async.db");
            using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => SqliteCipherHelper.OpenEncryptedAsync($"Data Source={path}"));

            Assert.Contains(path, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("could not re-initialise", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_refusal_says_what_to_do_about_it()
        {
            // A refusal an operator cannot act on is a crash with better grammar.
            var path = CreatePlainDatabase("held-advice.db");
            using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            var ex = Assert.ThrowsAny<Exception>(() => SqliteCipherHelper.OpenEncrypted($"Data Source={path}"));

            Assert.Contains("held by another process", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("move it aside", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_already_encrypted_store_is_not_reinitialised_at_all()
        {
            // The guard must not fire on the ordinary path. Open once to create it encrypted, then
            // open again: the second open must find a valid key and leave the file alone.
            var path = Path.Combine(_dir, "already.db");

            using (var first = SqliteCipherHelper.OpenEncrypted($"Data Source={path}"))
            {
                using var cmd = first.CreateCommand();
                cmd.CommandText = "CREATE TABLE keep (id INTEGER); INSERT INTO keep VALUES (7);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            using (var second = SqliteCipherHelper.OpenEncrypted($"Data Source={path}"))
            {
                using var cmd = second.CreateCommand();
                cmd.CommandText = "SELECT id FROM keep;";
                Assert.Equal(7L, Convert.ToInt64(cmd.ExecuteScalar()));
            }

            Assert.Empty(Asides("already.db"));
        }

        // ──────────────────────────────────────────────────────────────────
        //  The discriminator itself
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void The_discriminator_calls_a_plain_database_plaintext()
        {
            var path = CreatePlainDatabase("sniff-plain.db");

            Assert.Equal(
                SqliteCipherHelper.AsideContent.Plaintext,
                SqliteCipherHelper.ClassifyDatabaseFile(path));
        }

        [Fact]
        public void The_discriminator_calls_our_own_encrypted_store_unreadable()
        {
            // "Unreadable" is from the point of view of someone holding the file and no key. A
            // SQLCipher file opens with a random salt, so it can never collide with the header.
            var path = Path.Combine(_dir, "sniff-encrypted.db");
            using (var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={path}"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            Assert.Equal(
                SqliteCipherHelper.AsideContent.Unreadable,
                SqliteCipherHelper.ClassifyDatabaseFile(path));
        }

        [Fact]
        public void The_discriminator_calls_a_foreign_keyed_store_unreadable()
        {
            var path = CreateForeignKeyedDatabase("sniff-foreign.db");

            Assert.Equal(
                SqliteCipherHelper.AsideContent.Unreadable,
                SqliteCipherHelper.ClassifyDatabaseFile(path));
        }

        [Fact]
        public void The_discriminator_fails_safe_on_a_file_too_short_to_classify()
        {
            // The first eight bytes MATCH the plain header. A sniff that read what it could and
            // compared the prefix would call this plaintext and delete it; sixteen bytes is the
            // question, and anything less is not an answer.
            var path = Path.Combine(_dir, "short.db");
            File.WriteAllBytes(path, PlainHeader.Take(8).ToArray());

            Assert.Equal(
                SqliteCipherHelper.AsideContent.Unreadable,
                SqliteCipherHelper.ClassifyDatabaseFile(path));
        }

        [Fact]
        public void The_discriminator_fails_safe_on_an_empty_file()
        {
            var path = Path.Combine(_dir, "empty.db");
            File.WriteAllBytes(path, Array.Empty<byte>());

            Assert.Equal(
                SqliteCipherHelper.AsideContent.Unreadable,
                SqliteCipherHelper.ClassifyDatabaseFile(path));
        }

        [Fact]
        public void The_discriminator_fails_safe_when_the_sniff_itself_throws()
        {
            // Missing file: the classify must return the preserving answer, not propagate. An
            // exception escaping here would abort a re-init that was otherwise fine.
            var path = Path.Combine(_dir, "not-there.db");
            Assert.False(File.Exists(path));

            Assert.Equal(
                SqliteCipherHelper.AsideContent.Unreadable,
                SqliteCipherHelper.ClassifyDatabaseFile(path));
        }

        // ──────────────────────────────────────────────────────────────────
        //  The read-back that authorises the deletion
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void The_readback_refuses_a_store_that_does_not_read_back()
        {
            // Tested directly because no end-to-end failure can single this step out: every way of
            // breaking a re-init from outside the process breaks it at Open or at CommitEncryption,
            // both earlier than here. Without this test the read-back is asserted by nothing.
            var path = Path.Combine(_dir, "no-init-table.db");
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();

            var ex = Assert.Throws<IOException>(
                () => { SqliteCipherHelper.VerifyReinitialisedStore(conn, $"Data Source={path}"); });

            Assert.Contains("not trustworthy", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("nothing beside it was removed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_readback_accepts_a_store_that_does()
        {
            // The other half: a check that refuses everything authorises nothing and would have
            // made the deletion dead code.
            var path = Path.Combine(_dir, "with-init-table.db");
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE _sqlcipher_init (id INTEGER);";
                cmd.ExecuteNonQuery();
            }

            var readback = SqliteCipherHelper.VerifyReinitialisedStore(conn, $"Data Source={path}");

            // It records having read the marker back, which a default value does not. The deletion
            // refuses a reading without it, so a verify that returned one would quietly stop
            // authorising anything and the whole plaintext-cleanup half of the ruling would become
            // dead code that still compiled and still logged nothing wrong.
            Assert.True(readback.MarkerTableRead);

            // And the store really is empty, measured rather than assumed — this is the number the
            // "it came back EMPTY" line to the operator is conditioned on.
            Assert.Equal(0L, readback.OtherTables);
        }

        [Fact]
        public void The_readback_counts_the_tables_the_empty_claim_rests_on()
        {
            // Without this, OtherTables could be hard-zero and the EMPTY line would be a literal
            // dressed up as a measurement — the exact shape this file keeps having to remove.
            var path = Path.Combine(_dir, "not-actually-empty.db");
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "CREATE TABLE _sqlcipher_init (id INTEGER);"
                    + "CREATE TABLE accepted (id INTEGER);"
                    + "CREATE TABLE baselines (id INTEGER);";
                cmd.ExecuteNonQuery();
            }

            var readback = SqliteCipherHelper.VerifyReinitialisedStore(conn, $"Data Source={path}");

            // The marker itself is excluded (it is not the operator's data), and so are SQLite's
            // own sqlite_* tables — a count that included them would call every store non-empty.
            Assert.Equal(2L, readback.OtherTables);
        }

        // ──────────────────────────────────────────────────────────────────
        //  The head reading: three ways of not being a plain header, kept apart
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void The_head_reading_reports_a_measured_plain_header()
        {
            Assert.Equal(
                SqliteCipherHelper.FileHeadReading.PlainSqliteHeader,
                SqliteCipherHelper.ReadFileHead(CreatePlainDatabase("head-plain.db")));
        }

        [Fact]
        public void The_head_reading_reports_measured_bytes_that_are_not_the_header()
        {
            Assert.Equal(
                SqliteCipherHelper.FileHeadReading.OtherBytes,
                SqliteCipherHelper.ReadFileHead(CreateForeignKeyedDatabase("head-foreign.db")));
        }

        [Fact]
        public void The_head_reading_keeps_a_short_file_apart_from_an_unreadable_one()
        {
            // THE POINT OF THE TYPE. Both of these collapse to AsideContent.Unreadable, and while
            // they did so before reaching the log, the rename line printed "It is not a plain
            // SQLite file" over BOTH — a positive finding, for a file nothing had measured. Three
            // grounds, three amounts of knowledge; a two-valued answer cannot say which.
            var shortFile = Path.Combine(_dir, "head-short.db");
            File.WriteAllBytes(shortFile, PlainHeader.Take(8).ToArray());

            var missing = Path.Combine(_dir, "head-missing.db");
            Assert.False(File.Exists(missing));

            Assert.Equal(
                SqliteCipherHelper.FileHeadReading.TooShort,
                SqliteCipherHelper.ReadFileHead(shortFile));
            Assert.Equal(
                SqliteCipherHelper.FileHeadReading.Unmeasured,
                SqliteCipherHelper.ReadFileHead(missing));
        }

        // ──────────────────────────────────────────────────────────────────
        //  The deletion's own contract, driven directly
        //
        //  None of these are reachable end to end. The copy does not exist until the File.Move,
        //  and nothing can take a handle on it — or swap the store beside it — between the Move
        //  and the delete. Left to the end-to-end path, a mutation making the delete rethrow, and
        //  one removing the encryption gate, both passed the entire suite (2026-08-06).
        // ──────────────────────────────────────────────────────────────────

        /// <summary>An encrypted store at <paramref name="name"/>, as the successful re-init leaves one.</summary>
        private string CreateEncryptedStore(string name)
        {
            var path = Path.Combine(_dir, name);
            using (var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={path}"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            Assert.NotEqual(PlainHeader, Head(path, PlainHeader.Length));
            return path;
        }

        private static SqliteCipherHelper.ReinitAside PlaintextAside(string path) =>
            new(path, SqliteCipherHelper.AsideContent.Plaintext, null);

        /// <summary>
        /// The export reading a test that is not exercising the export hands the deletion: nothing
        /// was carried. Explicit rather than defaulted, because a caller that skipped the export
        /// and a caller that ran one and got nothing are the same on disk and must say so.
        /// </summary>
        private static SqliteCipherHelper.StoreExport CarriedNothing =>
            new(SqliteCipherHelper.ExportOutcome.NotAttempted, null, 0, 0, "this test did not run an export");

        private static SqliteCipherHelper.StoreReadback Verified =>
            new(MarkerTableRead: true, OtherTables: 0);

        /// <summary>
        /// The file name to look for in a RENDERED log line, not the full path.
        ///
        /// <para>⚠ Serilog renders a string property through WriteQuotedJsonString, so a Windows
        /// path comes out quoted AND backslash-escaped: <c>"C:\\Temp\\x.db"</c>. A test asserting
        /// <c>Contains(fullPath)</c> therefore fails for a line that names the file perfectly well
        /// — or, worse, is written to pass by weakening the assertion to something that no longer
        /// pins the file at all. The leaf name has no separators and survives the escaping.</para>
        /// </summary>
        private static string NameIn(string path) => Path.GetFileName(path);

        [Fact]
        public void POSITIVE_CONTROL_the_deletion_removes_the_copy_when_every_condition_is_measured()
        {
            // Without this the four refusals below could all pass because the deletion never
            // deletes anything at all.
            var store = CreateEncryptedStore("direct-ok.db");
            var copy = CreatePlainDatabase("direct-ok.db.copy");

            SqliteCipherHelper.RemoveVerifiedPlaintextAside(
                $"Data Source={store}", PlaintextAside(copy), Verified, CarriedNothing);

            Assert.False(File.Exists(copy));
        }

        [Fact]
        public void A_delete_that_fails_is_reported_at_Error_and_does_not_throw()
        {
            // RULING POINT 5. By the time this runs the encrypted store is open and usable; letting
            // a leftover file throw would surface out of one of 25 OpenEncrypted consumers and,
            // under the Windows SCM, turn a working store into a startup restart loop. A mutation
            // making this rethrow passed all 17 tests before this test existed.
            var store = CreateEncryptedStore("direct-locked.db");
            var copy = CreatePlainDatabase("direct-locked.db.copy");

            // FileShare without Delete is exactly how Windows refuses File.Delete.
            using var hold = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            try
            {
                // No assertion wrapper: a throw here fails the test, which is the assertion.
                SqliteCipherHelper.RemoveVerifiedPlaintextAside(
                    $"Data Source={store}", PlaintextAside(copy), Verified, CarriedNothing);
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            Assert.True(File.Exists(copy));

            // And it is LOUD, naming the file, because the honest claim is that a readable copy is
            // still sitting next to the store.
            var errors = captured.Snapshot(Serilog.Events.LogEventLevel.Error);
            Assert.Contains(errors, m => m.Contains(NameIn(copy)) && m.Contains("STILL THERE"));
        }

        [Fact]
        public void The_copy_is_kept_when_the_replacement_is_not_measured_as_encrypted()
        {
            // The delete is authorised by evidence of READABILITY but justified by ENCRYPTION, and
            // nothing in the read-back measures encryption: both of its reads succeed against a
            // plain store just as well. This file's own 2026-05-21 note records a real state where
            // a store materialised an UNENCRYPTED header after Open, and a provider mis-binding
            // (e_sqlite3 where e_sqlcipher was meant) produces the same. In that state the delete
            // would destroy the last readable copy on a security justification that is false.
            var plainStore = CreatePlainDatabase("direct-plain-store.db");
            var copy = CreatePlainDatabase("direct-plain-store.db.copy");

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            try
            {
                SqliteCipherHelper.RemoveVerifiedPlaintextAside(
                    $"Data Source={plainStore}", PlaintextAside(copy), Verified, CarriedNothing);
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            Assert.True(File.Exists(copy));

            var errors = captured.Snapshot(Serilog.Events.LogEventLevel.Error);
            Assert.Contains(errors, m =>
                m.Contains("NOT established that the replacement is encrypted"));
        }

        [Fact]
        public void A_replacement_store_that_cannot_be_sniffed_does_not_authorise_the_delete()
        {
            // "Did not come back plain" is not the same as "measured as not plain". A sniff that
            // fails establishes nothing, and nothing is what it may authorise — the same fail-safe
            // direction as the discriminator, pointed the other way, because here the cautious
            // answer is to keep the copy rather than to keep the file being sniffed.
            var missingStore = Path.Combine(_dir, "direct-no-store.db");
            Assert.False(File.Exists(missingStore));
            var copy = CreatePlainDatabase("direct-no-store.db.copy");

            SqliteCipherHelper.RemoveVerifiedPlaintextAside(
                $"Data Source={missingStore}", PlaintextAside(copy), Verified, CarriedNothing);

            Assert.True(File.Exists(copy));
        }

        [Fact]
        public void A_readback_that_never_happened_does_not_authorise_the_delete()
        {
            // default(StoreReadback) is what a caller that skipped the read-back would hand over.
            // Not a boundary — anyone editing the helper can write MarkerTableRead: true — and it
            // is not described as one; it catches the careless edit.
            var store = CreateEncryptedStore("direct-unverified.db");
            var copy = CreatePlainDatabase("direct-unverified.db.copy");

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            try
            {
                SqliteCipherHelper.RemoveVerifiedPlaintextAside(
                    $"Data Source={store}", PlaintextAside(copy), default, CarriedNothing);
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            Assert.True(File.Exists(copy));

            var errors = captured.Snapshot(Serilog.Events.LogEventLevel.Error);
            Assert.Contains(errors, m => m.Contains("without a completed read-back"));
        }

        // ──────────────────────────────────────────────────────────────────
        //  The ORDER inside the sequence, which is the substance of the ruling
        //
        //  This is the hole the adversarial pass found on 2026-08-06 and it is worth naming
        //  precisely. Every end-to-end failure breaks a re-init at Open or at CommitEncryption,
        //  both EARLIER than the read-back — so no end-to-end test can distinguish "read back,
        //  then delete" from "delete, then read back". The first attempt at closing it was a
        //  value passed from one to the other with a comment claiming the compiler enforced the
        //  order; it does not, the record is internal, and a forged value compiled and passed the
        //  whole suite. The order is a behaviour, so it is pinned by a test of the behaviour: a
        //  read-back that FAILS, over a copy that must survive it.
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void POSITIVE_CONTROL_the_sequence_removes_the_copy_when_the_readback_succeeds()
        {
            // Without this, the ordering test below passes for any sequence that never deletes.
            var store = CreateEncryptedStore("seq-ok.db");
            var copy = CreatePlainDatabase("seq-ok.db.copy");

            using var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={store}");
            SqliteCipherHelper.CompleteReinitialisation(
                conn, $"Data Source={store}", PlaintextAside(copy), CarriedNothing);

            Assert.False(File.Exists(copy));
        }

        [Fact]
        public void The_sequence_leaves_the_copy_when_the_readback_throws()
        {
            // The store here is a REAL SQLCipher file under a key this process does not hold, and
            // the connection has no key applied — so the read-back's first query throws, while the
            // store still sniffs as encrypted. Both halves matter: if the store were plain, the
            // encryption gate would refuse the delete and this test would pass without ever
            // exercising the order it exists to pin.
            var store = CreateForeignKeyedDatabase("seq-unreadable.db");
            var copy = CreatePlainDatabase("seq-unreadable.db.copy");
            var original = File.ReadAllBytes(copy);

            using var conn = new SqliteConnection($"Data Source={store}");
            conn.Open();

            Assert.ThrowsAny<Exception>(
                () => SqliteCipherHelper.CompleteReinitialisation(
                    conn, $"Data Source={store}", PlaintextAside(copy), CarriedNothing));

            Assert.True(File.Exists(copy));
            Assert.Equal(original, File.ReadAllBytes(copy));
        }

        // ──────────────────────────────────────────────────────────────────
        //  The log itself, which on this path IS the deliverable
        //
        //  Points 5 and 7 of the ruling are entirely about what the operator is told. Before these
        //  tests no line's wording, level or presence was asserted anywhere in this file, and two
        //  mutations that deleted a required line each passed 17/17.
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void The_successful_path_says_the_contents_were_carried_and_the_copy_is_gone()
        {
            // THE SENTENCE THIS EXISTS FOR, and 2026-08-08 INVERTED IT. Until the export, the only
            // honest thing to tell an operator on this path was that their store had come back
            // EMPTY. Now the contents cross, so the line has to say so — WITH THE COUNTS, because
            // "we carried it across" unmeasured is exactly the class of claim this file keeps
            // having to remove. The delete line changes with it: the copy is destroyed, and its
            // contents are NOT gone, and both halves are conditioned on the same measurement.
            var path = CreatePlainDatabase("log-success.db");

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={path}");
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            var all = captured.Snapshot(Serilog.Events.LogEventLevel.Verbose);

            // CreatePlainDatabase writes one table holding one row, so the counts are known.
            Assert.Contains(all, m =>
                m.Contains("CARRIED ITS CONTENTS ACROSS")
                && m.Contains("1 schema object(s) and 1 row(s)"));

            // And the copy's story is finished: destroyed, and its contents are somewhere.
            Assert.Contains(all, m =>
                m.Contains("Deleted the pre-re-init copy")
                && m.Contains("Its CONTENTS are not gone"));

            // The pre-export sentence must not survive on a path that carried the data. It said
            // the opposite of what happened, which is worse than saying nothing.
            Assert.DoesNotContain(all, m => m.Contains("CONTENTS are NOT preserved anywhere"));
            Assert.DoesNotContain(all, m => m.Contains("only surviving version"));
        }

        [Fact]
        public void The_empty_reinit_of_a_cache_store_still_says_it_came_back_empty()
        {
            // The other half of the sentence above, and the one the 2026-08-06 ruling was written
            // for: when the contents CANNOT be carried, the operator is still told, in the same
            // place, that the store is empty — and now also WHY nothing crossed.
            var path = CreateForeignKeyedDatabase("blocking-history.db");

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={path}");
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            var all = captured.Snapshot(Serilog.Events.LogEventLevel.Verbose);
            Assert.Contains(all, m =>
                m.Contains("it is EMPTY")
                && m.Contains("Nothing was carried across, because")
                && m.Contains("not the plain SQLite header"));

            // The copy is the unreadable population, so it is kept and no delete line is written
            // about it — and nothing may claim one was.
            Assert.Single(Asides("blocking-history.db"));
            Assert.DoesNotContain(all, m => m.Contains("Deleted the pre-re-init copy"));
        }

        [Fact]
        public void A_reinit_that_fails_after_the_rename_says_what_became_of_the_copy()
        {
            // The mid-story stop the ruling forbids: a log that ends at "Renamed the existing
            // SQLite file to X ... UNENCRYPTED and readable by anyone" and never says whether the
            // file survived. Deleting this closing line passed 17/17 before this test.
            //
            // ⚠ The store name is NOT the instrument — see the corrected note on
            // A_reinit_that_fails_after_the_rename_leaves_the_copy_untouched. A read-only caller
            // does not stop the export (it runs on the export's own connection), so a REFUSE store
            // reaches the rename here too; this test's subject is the closing line, not the half.
            var path = CreatePlainDatabase("changed-objects.db");

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            try
            {
                Assert.ThrowsAny<Exception>(
                    () => SqliteCipherHelper.OpenEncrypted($"Data Source={path};Mode=ReadOnly"));
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            var aside = Assert.Single(Asides("changed-objects.db"));
            var errors = captured.Snapshot(Serilog.Events.LogEventLevel.Error);
            Assert.Contains(errors, m => m.Contains(NameIn(aside)) && m.Contains("nothing was done to"));
        }

        [Fact]
        public void The_rename_line_does_not_claim_a_measurement_it_could_not_make()
        {
            // The unreadable population's rename line used to print "It is not a plain SQLite file"
            // for all three grounds that reach it — including the sniff that threw, where nothing
            // whatever had been measured. A fail-safe DEFAULT printed as a finding is the same
            // defect class as the line it replaced. This drives the one ground that is reachable
            // end to end (a real SQLCipher file under a key nobody here holds) and pins that its
            // sentence rests on bytes that were actually read.
            // consolidation-history.db keeps this on the EMPTY-OK half so the rename still happens; a
            // REFUSE store would throw before any line about a rename could be written.
            var path = CreateForeignKeyedDatabase("consolidation-history.db");

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={path}");
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            var aside = Assert.Single(Asides("consolidation-history.db"));
            var all = captured.Snapshot(Serilog.Events.LogEventLevel.Verbose);
            Assert.Contains(all, m =>
                m.Contains(NameIn(aside))
                && m.Contains("first 16 bytes were read and are NOT the plain SQLite header"));
        }

        // ══════════════════════════════════════════════════════════════════
        //  THE EXPORT (Adrian's ruling, 2026-08-08, point 1)
        //
        //  Everything above this line is about which COPY survives. Everything below is about the
        //  contents crossing, which is the change that makes most of the copies unnecessary.
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// A plain database holding one of everything a store can hold — rows with values, a
        /// plain index, a UNIQUE index, a view, a trigger, and an AUTOINCREMENT counter — so a
        /// migration that carries the ROWS and quietly drops the SCHEMA cannot pass.
        /// </summary>
        private string CreateRichPlainDatabase(string name)
        {
            var path = Path.Combine(_dir, name);
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE accepted (
                            id       INTEGER PRIMARY KEY AUTOINCREMENT,
                            finding  TEXT NOT NULL,
                            accepted_by TEXT NOT NULL);
                        CREATE INDEX idx_accepted_finding ON accepted(finding);
                        CREATE UNIQUE INDEX idx_accepted_by ON accepted(accepted_by);
                        CREATE TABLE accepted_audit (id INTEGER, note TEXT);
                        CREATE VIEW v_accepted AS SELECT finding FROM accepted;
                        CREATE TRIGGER trg_accepted AFTER INSERT ON accepted
                            BEGIN INSERT INTO accepted_audit(id, note) VALUES (new.id, 'accepted'); END;";
                    cmd.ExecuteNonQuery();
                }
                for (var i = 1; i <= 25; i++)
                {
                    using var ins = conn.CreateCommand();
                    ins.CommandText = "INSERT INTO accepted(finding, accepted_by) VALUES ($f, $w);";
                    ins.Parameters.AddWithValue("$f", "finding-" + i);
                    ins.Parameters.AddWithValue("$w", "dba-" + i);
                    ins.ExecuteNonQuery();
                }
            }
            SqliteConnection.ClearAllPools();
            Assert.Equal(PlainHeader, Head(path, PlainHeader.Length));
            return path;
        }

        /// <summary>Every row of the accepted table, rendered so a comparison is about VALUES.</summary>
        private static string[] AcceptedRows(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id || '|' || finding || '|' || accepted_by FROM accepted ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            var rows = new System.Collections.Generic.List<string>();
            while (reader.Read()) rows.Add(reader.GetString(0));
            return rows.ToArray();
        }

        /// <summary>Every schema object, so an index or a trigger going missing is visible.</summary>
        private static string[] SchemaObjects(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT type || ':' || name FROM sqlite_master WHERE name NOT LIKE 'sqlite%' "
                + "AND name <> '_sqlcipher_init' ORDER BY type, name;";
            using var reader = cmd.ExecuteReader();
            var objects = new System.Collections.Generic.List<string>();
            while (reader.Read()) objects.Add(reader.GetString(0));
            return objects.ToArray();
        }

        [Fact]
        public void The_migration_carries_the_rows_their_values_and_the_schema_across()
        {
            // THE POINT OF THE WHOLE CHANGE. Before 2026-08-08 this store came back EMPTY, and for
            // check-baselines.db the emptiness was the event.
            var path = CreateRichPlainDatabase("check-baselines.db");

            string[] beforeRows, beforeObjects;
            using (var before = new SqliteConnection($"Data Source={path}"))
            {
                before.Open();
                beforeRows = AcceptedRows(before);
                beforeObjects = SchemaObjects(before);
            }
            SqliteConnection.ClearAllPools();
            Assert.Equal(25, beforeRows.Length);

            using (var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={path}"))
            {
                // Values, not just a count: a migration that inserted 25 empty rows would pass a
                // count and fail here.
                Assert.Equal(beforeRows, AcceptedRows(conn));

                // Indexes, the view and the trigger — sqlite_master rows, compared as a set.
                Assert.Equal(beforeObjects, SchemaObjects(conn));
                Assert.Contains("index:idx_accepted_finding", SchemaObjects(conn));
                Assert.Contains("index:idx_accepted_by", SchemaObjects(conn));
                Assert.Contains("trigger:trg_accepted", SchemaObjects(conn));
                Assert.Contains("view:v_accepted", SchemaObjects(conn));

                // The trigger's OWN OUTPUT crossed as well: it fired on all 25 setup inserts, and
                // those audit rows are data like any other.
                long auditBefore;
                using (var audit = conn.CreateCommand())
                {
                    audit.CommandText = "SELECT count(*) FROM accepted_audit WHERE note = 'accepted';";
                    auditBefore = Convert.ToInt64(audit.ExecuteScalar());
                }
                Assert.Equal(25L, auditBefore);

                // And it is not merely PRESENT, it FIRES — a trigger carried across as a
                // sqlite_master row that no longer runs is a schema that looks right and is not.
                using (var ins = conn.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO accepted(finding, accepted_by) VALUES ('after','dba-after');";
                    ins.ExecuteNonQuery();
                }
                using (var audit = conn.CreateCommand())
                {
                    audit.CommandText = "SELECT count(*) FROM accepted_audit WHERE note = 'accepted';";
                    Assert.Equal(auditBefore + 1, Convert.ToInt64(audit.ExecuteScalar()));
                }

                // The AUTOINCREMENT counter crossed too (sqlite_sequence), so the next id
                // continues the operator's numbering instead of colliding with it.
                using (var seq = conn.CreateCommand())
                {
                    seq.CommandText = "SELECT id FROM accepted WHERE finding = 'after';";
                    Assert.Equal(26L, Convert.ToInt64(seq.ExecuteScalar()));
                }

                // And the UNIQUE index constrains, which is the other half of "carried across".
                using var dup = conn.CreateCommand();
                dup.CommandText = "INSERT INTO accepted(finding, accepted_by) VALUES ('dup','dba-1');";
                Assert.ThrowsAny<SqliteException>(() => dup.ExecuteNonQuery());
            }

            // The store that holds all this is encrypted, and the unencrypted copy is gone.
            Assert.NotEqual(PlainHeader, Head(path, PlainHeader.Length));
            Assert.Empty(Asides("check-baselines.db"));

            // No temp file outlived the migration.
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Fact]
        public void The_async_path_carries_the_contents_across_too()
        {
            // Two entry points, one ruling. The export is the half that leaves evidence on disk.
            var path = CreateRichPlainDatabase("change-items.db");

            using (var conn = await_open(path))
            {
                Assert.Equal(25, AcceptedRows(conn).Length);
                Assert.Contains("trigger:trg_accepted", SchemaObjects(conn));
            }

            Assert.Empty(Asides("change-items.db"));
            Assert.NotEqual(PlainHeader, Head(path, PlainHeader.Length));

            static SqliteConnection await_open(string p) =>
                SqliteCipherHelper.OpenEncryptedAsync($"Data Source={p}").GetAwaiter().GetResult();
        }

        // ───────────────────────────────────────────────────────────────────
        //  THE GATE THAT AUTHORISES THE EXPORT
        //
        //  ⚠ THESE EXIST BECAUSE OF A SURVIVED MUTATION (2026-08-08). Two mutations passed all 58
        //  tests: one that made the source-vs-copy comparison always succeed, and one that used the
        //  source's own fingerprint in place of reading the copy's. Both delete the gate outright.
        //  Nothing noticed, because every test above drove an export that genuinely worked — the
        //  gate was exercised on the happy path only, which is the same as not being exercised.
        //
        //  A wrong export cannot be provoked from outside the process, so the gate is driven
        //  directly against a copy that has been made wrong on purpose. The copy is made wrong by
        //  changing the SOURCE after a real export, which needs no key and models the real failure
        //  (the two disagree) rather than a manufactured one.
        // ───────────────────────────────────────────────────────────────────

        [Fact]
        public void POSITIVE_CONTROL_a_faithful_export_verifies_and_reports_what_it_counted()
        {
            // Without this, the two refusals below would pass against a gate that refuses
            // everything — which would make the whole export half of the ruling dead code that
            // still compiled and still logged nothing wrong.
            var source = CreateRichPlainDatabase("verify-control.db");

            var export = SqliteCipherHelper.TryExportPlaintextStore(source);

            Assert.Equal(SqliteCipherHelper.ExportOutcome.Exported, export.Outcome);
            Assert.True(File.Exists(export.TempPath));

            // COUNTED, and the numbers are the ones the operator is told. Six schema objects (two
            // tables, two indexes, a view and a trigger) and fifty rows (25 accepted + the 25 the
            // trigger wrote into accepted_audit).
            Assert.Equal(6L, export.Objects);
            Assert.Equal(50L, export.Rows);
            Assert.Null(export.Detail);

            // Re-checking a copy that is still faithful says the same thing and destroys nothing.
            var second = SqliteCipherHelper.VerifyExportedCopy(source, export.TempPath!);
            Assert.Equal(SqliteCipherHelper.ExportOutcome.Exported, second.Outcome);
            Assert.True(File.Exists(export.TempPath));

            SqliteConnection.ClearAllPools();
            File.Delete(export.TempPath!);
        }

        [Fact]
        public void An_export_that_does_not_hold_every_row_is_refused_and_deleted()
        {
            var source = CreateRichPlainDatabase("verify-rows.db");
            var export = SqliteCipherHelper.TryExportPlaintextStore(source);
            Assert.Equal(SqliteCipherHelper.ExportOutcome.Exported, export.Outcome);
            var copy = export.TempPath!;

            // One more row in the source than in the copy — which is what an export interrupted
            // part-way through the data, or one that silently truncated, looks like.
            using (var conn = new SqliteConnection($"Data Source={source}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO accepted(finding, accepted_by) VALUES ('later','dba-later');";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            SqliteCipherHelper.StoreExport verdict;
            try
            {
                verdict = SqliteCipherHelper.VerifyExportedCopy(source, copy);
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            Assert.Equal(SqliteCipherHelper.ExportOutcome.Failed, verdict.Outcome);
            Assert.Null(verdict.TempPath);

            // The numbers are in the reason, so a reader is told WHAT disagreed. The trigger fires
            // on the new row too, so the source moved from 50 to 52.
            Assert.Contains("the source holds 6 schema object(s) and 52 row(s), the export holds 6 and 50",
                verdict.Detail!, StringComparison.Ordinal);

            // And the copy is GONE, because a copy that cannot be trusted must not be able to be
            // moved into the store's place by a later step that forgot to look.
            Assert.False(File.Exists(copy));

            var errors = captured.Snapshot(Serilog.Events.LogEventLevel.Error);
            Assert.Contains(errors, m => m.Contains("Refusing the export of"));
        }

        [Fact]
        public void An_export_that_does_not_hold_every_schema_object_is_refused_and_deleted()
        {
            // The other half, and it is a separate test on purpose: a gate that compared only row
            // totals would pass the one above and fail here, and a store whose UNIQUE index went
            // missing accepts duplicates from the next write onwards.
            var source = CreateRichPlainDatabase("verify-schema.db");
            var export = SqliteCipherHelper.TryExportPlaintextStore(source);
            Assert.Equal(SqliteCipherHelper.ExportOutcome.Exported, export.Outcome);
            var copy = export.TempPath!;

            // An index in the source that the copy does not have — no row counts change.
            using (var conn = new SqliteConnection($"Data Source={source}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE INDEX idx_accepted_by_who ON accepted(accepted_by, finding);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var verdict = SqliteCipherHelper.VerifyExportedCopy(source, copy);

            Assert.Equal(SqliteCipherHelper.ExportOutcome.Failed, verdict.Outcome);
            Assert.Contains("the source holds 7 schema object(s) and 50 row(s), the export holds 6 and 50",
                verdict.Detail!, StringComparison.Ordinal);
            Assert.False(File.Exists(copy));
        }

        [Fact]
        public void An_export_the_store_key_cannot_open_is_refused_and_deleted()
        {
            // The silent trap probe (b) measured: two ATTACH KEY spellings export without an error
            // and leave a store PRAGMA key = "x'HEX'" cannot open. Staged from the other end — a
            // real SQLCipher file under a key this process does not hold is byte-for-byte what such
            // an export produces — because the wrong spelling cannot be reached from a test.
            var source = CreateRichPlainDatabase("verify-wrong-key.db");
            var copy = CreateForeignKeyedDatabase("verify-wrong-key.db.reinit-20260808010203004.tmp");

            var verdict = SqliteCipherHelper.VerifyExportedCopy(source, copy);

            Assert.Equal(SqliteCipherHelper.ExportOutcome.Failed, verdict.Outcome);
            Assert.False(File.Exists(copy));

            // The source is untouched — a gate that refuses must not also destroy.
            Assert.True(File.Exists(source));
            Assert.Equal(PlainHeader, Head(source, PlainHeader.Length));
        }

        [Fact]
        public void A_wrong_keyed_refuse_store_is_refused_rather_than_emptied()
        {
            // The population an export CANNOT rescue: a real SQLCipher file under a key this
            // install does not hold, which is what a DPAPI regeneration leaves behind. seat-register.db
            // is a licence record; handing it back empty is a commercial fact, not a cache miss.
            var path = CreateForeignKeyedDatabase("seat-register.db");
            var original = File.ReadAllBytes(path);

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            Exception? thrown = null;
            try
            {
                thrown = Assert.ThrowsAny<Exception>(
                    () => SqliteCipherHelper.OpenEncrypted($"Data Source={path}"));
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            // The refusal names the store and says what state the disk is in, because the whole
            // value of refusing is that an operator can act on it.
            Assert.Contains("seat-register.db", thrown!.Message, StringComparison.Ordinal);
            Assert.Contains(
                "THIS RUN HAS NOT RENAMED, MOVED, COPIED OR DELETED IT", thrown.Message, StringComparison.Ordinal);

            // ⚠ AND NOT "NOTHING HAS BEEN CHANGED ON DISK", which is what this sentence said until
            // 2026-08-08 and what this test asserted with the comment "And it is true. The file is
            // byte-for-byte what it was". It is true HERE — this instrument is a store with no WAL
            // — and false for the population as a whole, which is the shape of every over-claim
            // this file has had to remove: a universal proved on the sub-population where it
            // happens to hold. The_refusal_does_not_claim_a_file_it_rewrote_is_unchanged drives the
            // half where the bytes DO change and pins that the sentence survives it.
            Assert.DoesNotContain("NOTHING HAS BEEN CHANGED ON DISK", thrown.Message, StringComparison.Ordinal);

            // What is asserted here is what the run did: no rename, no replacement, nothing to put
            // back — and, for this store, the bytes happen to be identical too.
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(Asides("seat-register.db"));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

            var errors = captured.Snapshot(Serilog.Events.LogEventLevel.Error);
            Assert.Contains(errors, m => m.Contains("will not re-initialise"));

            // ⚠ AND THE PROBE LINE ABOVE IT STOPS AT THE CLASSIFICATION. IsKeyValid used to end
            // "plain/wrong-key/corrupt file; re-initialising", so this very log read, consecutively,
            // "… re-initialising" and then "SQLTriage will not re-initialise the encrypted store
            // 'seat-register.db'". IsKeyValid cannot know the outcome at that point — the fallback
            // registry decides it — so it no longer claims one.
            var all = captured.Snapshot(Serilog.Events.LogEventLevel.Verbose);
            var probeLine = Assert.Single(all.Where(m => m.Contains("Probe failed for")));
            Assert.Contains("plain, keyed differently, or corrupt", probeLine, StringComparison.Ordinal);
            Assert.DoesNotContain("re-initialising", probeLine, StringComparison.Ordinal);
        }

        [Fact]
        public void The_refusal_does_not_claim_a_file_it_rewrote_is_unchanged()
        {
            // THE SIXTEENTH INSTANCE OF THIS TREE'S OVER-CLAIM CLASS, and the measurement that
            // falsifies the old sentence was already in the file 550 lines above the sentence:
            // probe (i) records that the key probe OPENS the database, and that closing that
            // connection checkpoints an uncheckpointed -wal into the main file and deletes it —
            // for a store encrypted under a key this install does not hold, after the probe has
            // already failed with SQLITE_NOTADB. So "NOTHING HAS BEEN CHANGED ON DISK" was printed
            // over a file two of whose bytes-on-disk facts had just changed.
            //
            // check-baselines.db, foreign-keyed, with a hot -wal: the REFUSE population in exactly
            // the state the old claim was false for.
            var (main, wal) = CreateStoreWithHotWal("check-baselines.db", encryptedUnderForeignKey: true);
            var before = File.ReadAllBytes(main);
            Assert.True(File.Exists(wal));

            var thrown = Assert.ThrowsAny<Exception>(
                () => SqliteCipherHelper.OpenEncrypted($"Data Source={main}"));

            // THE CONTROL, and the whole test rests on it: the probe really did rewrite the file.
            // If this ever stops being true the assertion below is being made about a population
            // where the old universal held, and this test is measuring nothing.
            Assert.NotEqual(before, File.ReadAllBytes(main));
            Assert.False(File.Exists(wal));

            // The refusal still ran, still refused, and says only what this run did.
            Assert.Contains("will not re-initialise", thrown.Message, StringComparison.Ordinal);
            Assert.Contains(
                "THIS RUN HAS NOT RENAMED, MOVED, COPIED OR DELETED IT", thrown.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("NOTHING HAS BEEN CHANGED ON DISK", thrown.Message, StringComparison.Ordinal);

            // And it says out loud that the two are not the same thing, rather than leaving an
            // operator to discover it from a checksum.
            Assert.Contains("not the same as byte-for-byte untouched", thrown.Message, StringComparison.Ordinal);

            // The claim it DOES make is true: the file is still at its own path, under its own
            // name, with nothing put in its place.
            Assert.True(File.Exists(main));
            Assert.Empty(Asides("check-baselines.db"));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Fact]
        public void An_empty_ok_store_whose_export_fails_loses_the_copy_and_says_so()
        {
            // ⚠ THE ONE PATH THAT DELETES THE ASIDE WITHOUT CARRYING ANYTHING, pinned here because
            // the class doc used to deny it existed: "The aside is the rollback source. A failure
            // ANYWHERE leaves it." Measured false on 2026-08-08 — the delete gate turns on the copy
            // being readable-by-anyone and the replacement working, NOT on the export having
            // carried anything, so an EMPTY-OK store whose file classifies as plaintext and whose
            // export fails is renamed aside, re-initialised empty, and the copy is destroyed.
            //
            // Ruling point 3 keeps that behaviour for this population, so what is asserted is the
            // behaviour AND the sentence the operator gets, and the class doc now names the
            // exception instead of a universal. If a later edit makes the aside survive here, this
            // fails and the doc has to change with it.
            //
            // The instrument is a file that classifies plaintext and cannot be exported: the exact
            // 16-byte plain header, then bytes no database engine will read.
            var path = Path.Combine(_dir, "blocking-history.db");
            var bytes = new byte[8192];
            Array.Copy(PlainHeader, bytes, PlainHeader.Length);
            for (var i = PlainHeader.Length; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
            File.WriteAllBytes(path, bytes);
            Assert.Equal(PlainHeader, Head(path, PlainHeader.Length));

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={path}");
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
                cmd.ExecuteScalar();
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            var all = captured.Snapshot(Serilog.Events.LogEventLevel.Verbose);

            // THE CONTROL: the export was tried on this file and failed. Without it the assertions
            // below could be measuring a run that never attempted to carry anything.
            Assert.Contains(all, m => m.Contains("Could not export the contents of", StringComparison.Ordinal));

            // The copy is GONE — this is the exception the class doc now states.
            Assert.Empty(Asides("blocking-history.db"));

            // And the line an operator gets says so in as many words, rather than reporting a
            // successful re-initialisation over a destroyed file.
            Assert.Contains(all, m =>
                m.Contains("The copy's CONTENTS are NOT preserved anywhere", StringComparison.Ordinal)
                && m.Contains("gone with it", StringComparison.Ordinal));

            // The replacement is real and encrypted, which is what authorised the delete.
            Assert.NotEqual(PlainHeader, Head(path, PlainHeader.Length));
        }

        [Fact]
        public void A_half_written_export_left_by_a_killed_run_is_refused_and_rebuilt()
        {
            // ⚠ THE STATE THIS RECONSTRUCTS WAS MEASURED, not imagined. On 2026-08-08 a child
            // process was hard-killed ~120 ms into exporting a 2,000,000-row database. What it left
            // was an 8 MB destination that is NOT a plain header, that IsKeyValid's read probe AND
            // commit probe both accept — so IsKeyValid calls it valid — and that holds ZERO rows,
            // because the hot journal rolled the row copy back and left the schema committed.
            // Nothing about it looks wrong.
            //
            // A kill cannot be staged inside a test process, so the DISK STATE it leaves is staged
            // instead: the source still at its own path (which is what the export-before-rename
            // ordering guarantees) and a half-written temp beside it. What is asserted is that the
            // next run never adopts the half-written one.
            var path = CreateRichPlainDatabase("alert-history.db");

            var abandoned = path + ".reinit-20260808010203004.tmp";
            using (var partial = SqliteCipherHelper.OpenEncrypted($"Data Source={abandoned}"))
            {
                // The schema, with none of the rows — exactly what the kill left.
                using var cmd = partial.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE accepted (id INTEGER PRIMARY KEY AUTOINCREMENT, finding TEXT NOT NULL, accepted_by TEXT NOT NULL);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            Assert.True(File.Exists(abandoned));
            Assert.NotEqual(PlainHeader, Head(abandoned, PlainHeader.Length));

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={path}");

                // THE ASSERTION. Twenty-five rows, not the zero the abandoned export holds.
                Assert.Equal(25, AcceptedRows(conn).Length);
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            // The half-written file is gone, not adopted and not accumulating.
            Assert.False(File.Exists(abandoned));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

            var all = captured.Snapshot(Serilog.Events.LogEventLevel.Verbose);
            Assert.Contains(all, m =>
                m.Contains(NameIn(abandoned)) && m.Contains("a run that did not finish"));
        }

        [Fact]
        public void A_reinit_that_cannot_finish_leaves_the_source_where_it_was()
        {
            // The other half of the kill scenario, and the reason the export runs BEFORE the
            // rename: if the run dies while the replacement is being built, the operator's file has
            // not moved. The failure is staged at the step after the export — the store name is on
            // the REFUSE list and the file is one nothing here can read, so the run stops before
            // touching anything.
            var path = CreateForeignKeyedDatabase("server-config-baselines.db");
            var original = File.ReadAllBytes(path);

            Assert.ThrowsAny<Exception>(() => SqliteCipherHelper.OpenEncrypted($"Data Source={path}"));

            Assert.True(File.Exists(path));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(Asides("server-config-baselines.db"));
        }

        // ──────────────────────────────────────────────────────────────────
        //  The WAL siblings (Adrian's ruling, 2026-08-08, point 4)
        //
        //  Until this date TryClearDbFile renamed the main file aside and DELETED its -wal/-shm.
        //  Probe (h), 2026-08-08: a WAL-mode store with an uncheckpointed WAL had its ENTIRE schema
        //  and all 5000 of its rows in the -wal — a copy of the main file alone read back
        //  "no such table". The "preserved" copy could be an empty database.
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// A store whose data is in an UNCHECKPOINTED -wal and not in its main file, with no live
        /// handle on it.
        ///
        /// <para>Reached from the other end, because it cannot be reached from this one: closing
        /// the last connection to a WAL database checkpoints it, which is precisely the state this
        /// needs to avoid. So the files are COPIED out from under a connection that is still open
        /// with autocheckpoint disabled, and that connection is then thrown away with its
        /// directory. What lands here is byte-for-byte what a killed or crashed process leaves.</para>
        /// </summary>
        private (string Main, string Wal) CreateStoreWithHotWal(string name, bool encryptedUnderForeignKey)
        {
            var staging = Path.Combine(_dir, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var stagedMain = Path.Combine(staging, "staged.db");

            var holder = new SqliteConnection($"Data Source={stagedMain}");
            holder.Open();
            if (encryptedUnderForeignKey)
            {
                using var key = holder.CreateCommand();
                key.CommandText = $"PRAGMA key = \"x'{ForeignHexKey}'\";";
                key.ExecuteNonQuery();
            }
            using (var pragma = holder.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
                pragma.ExecuteNonQuery();
            }
            using (var cmd = holder.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE accepted (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, finding TEXT NOT NULL, accepted_by TEXT NOT NULL);
                    WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 500)
                    INSERT INTO accepted(finding, accepted_by) SELECT 'finding-' || x, 'dba-' || x FROM n;";
                cmd.ExecuteNonQuery();
            }

            var main = Path.Combine(_dir, name);
            var wal = main + "-wal";
            File.Copy(stagedMain, main);
            File.Copy(stagedMain + "-wal", wal);

            holder.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(staging, recursive: true); } catch { /* teardown */ }

            // The instrument has to BE what it is named after: the data must be in the -wal and NOT
            // in the main file, or every assertion below is about a file that never needed its
            // sibling.
            Assert.True(new FileInfo(wal).Length > 0);
            if (!encryptedUnderForeignKey)
            {
                using var mainOnly = new SqliteConnection($"Data Source={Path.Combine(_dir, "main-only-probe.db")}");
                File.Copy(main, Path.Combine(_dir, "main-only-probe.db"), overwrite: true);
                mainOnly.Open();
                using var probe = mainOnly.CreateCommand();
                probe.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'accepted';";
                Assert.Equal(0L, Convert.ToInt64(probe.ExecuteScalar()));
            }
            SqliteConnection.ClearAllPools();
            try { File.Delete(Path.Combine(_dir, "main-only-probe.db")); } catch { /* teardown */ }

            return (main, wal);
        }

        /// <summary>Reads the accepted table out of a store, under a key if one is needed.</summary>
        private static string RowCount(string path, string? hexKey)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={path}");
                conn.Open();
                if (hexKey != null)
                {
                    using var key = conn.CreateCommand();
                    key.CommandText = $"PRAGMA key = \"x'{hexKey}'\";";
                    key.ExecuteNonQuery();
                }
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM accepted;";
                return Convert.ToInt64(cmd.ExecuteScalar()).ToString();
            }
            catch (SqliteException ex) { return "unreadable: " + ex.Message; }
            finally { SqliteConnection.ClearAllPools(); }
        }

        [Fact]
        public void The_copy_of_an_unreadable_wal_mode_store_still_holds_its_rows()
        {
            // THE PROPERTY, not the mechanism. The copy this class calls the rollback source has to
            // BE one, and for a store whose data is in an uncheckpointed -wal that is a real
            // question — probe (h) measured such a store whose main file alone read back
            // "no such table".
            var (main, wal) = CreateStoreWithHotWal("consolidation-history.db", encryptedUnderForeignKey: true);
            Assert.True(new FileInfo(wal).Length > 0);

            // THE CONTROL: the main file alone, as it stands right now, holds nothing. Without this
            // the assertion below would pass for a store that never needed its sibling at all.
            var beforeMainOnly = Path.Combine(_dir, "before-main-only.db");
            File.Copy(main, beforeMainOnly);
            Assert.StartsWith("unreadable:", RowCount(beforeMainOnly, ForeignHexKey));

            using (var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={main}"))
            {
                Assert.Equal(System.Data.ConnectionState.Open, conn.State);
            }

            // The copy reads back every row under its rightful key — from the aside plus whatever
            // siblings travelled with it, which is exactly what an operator renaming it back has.
            var aside = Assert.Single(Asides("consolidation-history.db"));
            var recovered = Path.Combine(_dir, "recovered.db");
            File.Copy(aside, recovered);
            foreach (var sidecar in AsideSidecars("consolidation-history.db"))
                File.Copy(sidecar, recovered + sidecar.Substring(aside.Length));

            Assert.Equal("500", RowCount(recovered, ForeignHexKey));

            // ⚠ AND NOT A BYTE COMPARISON of the copy against the original, which is what this
            // test asserted first and which FAILED. Probe (i), 2026-08-08: the helper's own key
            // probe opens the database, which runs WAL recovery, and closing that connection
            // CHECKPOINTS the WAL into the main file and deletes it — even though the probe had
            // already failed with SQLITE_NOTADB, and even for a store encrypted under a key this
            // install does not hold. The file is rewritten by being looked at. "The copy is
            // untouched" is therefore false for a store in this state, and the honest claim — the
            // one asserted above — is that the copy still WORKS.
        }

        [Fact]
        public void The_sidecar_move_keeps_the_copy_complete_when_no_checkpoint_folded_the_wal_in()
        {
            // The state MoveSidecarsBeside exists for, and probe (i) is the reason it has to be
            // driven directly: the helper's own key probe checkpoints a hot WAL away on its way
            // past, so no end-to-end path can present the rename with one. The state is real — a
            // checkpoint needs the close to be the LAST connection and needs to succeed, and the
            // contended file is precisely the case this whole re-init path exists to handle — so
            // it is staged rather than provoked.
            var (main, wal) = CreateStoreWithHotWal("uptime.db", encryptedUnderForeignKey: true);
            Assert.True(File.Exists(wal));

            // THE CONTROL: what the pre-2026-08-08 behaviour preserved — the main file, alone.
            var mainOnly = Path.Combine(_dir, "main-only.db");
            File.Copy(main, mainOnly);
            Assert.StartsWith("unreadable:", RowCount(mainOnly, ForeignHexKey));

            var aside = main + ".pre-reinit-20260808010203004";
            File.Move(main, aside);
            var siblings = SqliteCipherHelper.MoveSidecarsBeside(main, aside);

            Assert.Empty(siblings.Lost);
            Assert.Empty(siblings.Copied);      // a free file is RENAMED; the copy is the fallback
            Assert.Contains(aside + "-wal", siblings.Beside);
            Assert.False(File.Exists(wal));

            // And the copy, with what travelled with it, is a working database again.
            Assert.Equal("500", RowCount(aside, ForeignHexKey));
        }

        [Fact]
        public void A_sidecar_that_cannot_be_renamed_is_COPIED_beside_the_copy_instead()
        {
            // ⚠ THE FIX THAT ACTUALLY PRESERVES ANYTHING (2026-08-08). "The sweep no longer deletes
            // a sibling it could not move" sounds like preservation and, on its own, is not: probe
            // (j) measured a stale -wal left at the store path being DISCARDED by SQLite the moment
            // the re-init creates a new database there. What saves those pages is getting a copy of
            // them beside the aside, and probe (k) measured the asymmetry that makes it possible —
            // on a file another handle holds with FileShare.Read, File.Move throws and File.Copy
            // succeeds, because a rename needs FILE_SHARE_DELETE from every open handle.
            //
            // That share mode is the instrument here, and it is the realistic one: the contended
            // sibling is the case this whole re-init path exists for.
            var (main, wal) = CreateStoreWithHotWal("uptime.db", encryptedUnderForeignKey: true);
            var aside = main + ".pre-reinit-20260808010203005";
            File.Move(main, aside);

            // THE CONTROL: the copy without its sibling is an empty database wearing the name.
            Assert.StartsWith("unreadable:", RowCount(aside, ForeignHexKey));

            SqliteCipherHelper.SidecarOutcome siblings;
            using (var holder = new FileStream(wal, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                siblings = SqliteCipherHelper.MoveSidecarsBeside(main, aside);
            }

            // The rename could not run, the copy could, and the sibling is beside the copy.
            Assert.Empty(siblings.Lost);
            Assert.Contains(aside + "-wal", siblings.Copied);
            Assert.Contains(aside + "-wal", siblings.Beside);
            Assert.True(File.Exists(wal));   // a copy leaves the original where it was

            // THE PROPERTY: the copy is a working database again, which is the only thing any of
            // this is for.
            Assert.Equal("500", RowCount(aside, ForeignHexKey));
        }

        [Fact]
        public void The_sweep_deletes_ordinary_leftovers_and_never_the_one_that_is_the_copy()
        {
            // The sweep at the end of TryClearDbFile deleted EVERY sibling still at the store's own
            // path — including the one MoveSidecarsBeside had just reported it could neither rename
            // nor copy, one statement after the rename line told the operator that file was "still
            // at the store's own path ... so the copy is missing whatever was in them". Measured on
            // 2026-08-08 with the move made to fail and the delete left to succeed: the log named
            // the file and the next probe found it gone.
            //
            // Driven directly. The state needs a File.Move that fails where a File.Delete would
            // succeed, against a destination name the helper builds from the clock at the instant
            // of the rename, so no end-to-end path can stage it.
            var store = Path.Combine(_dir, "swept.db");
            File.WriteAllBytes(store + "-wal", new byte[] { 1, 2, 3 });
            File.WriteAllBytes(store + "-shm", new byte[] { 4, 5, 6 });
            File.WriteAllBytes(store + "-journal", new byte[] { 7, 8, 9 });

            SqliteCipherHelper.RemoveStrandedSidecars(store, new[] { store + "-wal" });

            // The one that is the copy is left; the two that are debris are gone.
            Assert.True(File.Exists(store + "-wal"));
            Assert.False(File.Exists(store + "-shm"));
            Assert.False(File.Exists(store + "-journal"));

            // POSITIVE CONTROL, in the same test because the assertion above is meaningless without
            // it: with nothing preserved, the same file IS swept — so what is measured above is the
            // exemption working, not the sweep failing to run.
            File.WriteAllBytes(store + "-shm", new byte[] { 4, 5, 6 });
            SqliteCipherHelper.RemoveStrandedSidecars(store, Array.Empty<string>());
            Assert.False(File.Exists(store + "-wal"));
            Assert.False(File.Exists(store + "-shm"));
        }

        [Fact]
        public void The_sweep_survives_a_default_outcome_with_no_arrays_in_it()
        {
            // SidecarOutcome is a struct, so default(SidecarOutcome) holds three NULL arrays, and
            // this sweep is reached on the path where the rename never ran. A NullReferenceException
            // here is the data-loss guard not running at all, so the null is handled rather than
            // assumed away.
            var store = Path.Combine(_dir, "defaulted.db");
            File.WriteAllBytes(store + "-wal", new byte[] { 1 });

            SqliteCipherHelper.RemoveStrandedSidecars(store, default(SqliteCipherHelper.SidecarOutcome).Lost);

            Assert.False(File.Exists(store + "-wal"));
        }

        [Fact]
        public void The_export_reads_the_rows_that_are_only_in_the_wal()
        {
            // The same state on the PLAINTEXT half, where the sibling matters for a different
            // reason: the export opens the source normally and therefore reads through its WAL, so
            // rows that never reached the main file still cross. If it did not, this store would
            // migrate to a database with the right name and none of the data.
            var (main, _) = CreateStoreWithHotWal("changed-objects.db", encryptedUnderForeignKey: false);

            using (var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={main}"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM accepted;";
                Assert.Equal(500L, Convert.ToInt64(cmd.ExecuteScalar()));
            }

            Assert.NotEqual(PlainHeader, Head(main, PlainHeader.Length));
            Assert.Empty(Asides("changed-objects.db"));
        }

        [Fact]
        public void Deleting_an_unencrypted_copy_deletes_its_unencrypted_siblings_too()
        {
            // A -wal is the same plaintext as the file it belongs to — probe (h) measured one
            // holding an entire database — so a delete that removes the main copy and leaves its
            // siblings has not removed the readable copy, it has just made it less obvious.
            //
            // Driven directly, because the end-to-end path cannot reach it: the export opens the
            // plain source, and closing that connection checkpoints and removes the WAL before the
            // rename ever runs. There is no way in from outside.
            var store = CreateEncryptedStore("sidecar-delete.db");
            var copy = CreatePlainDatabase("sidecar-delete.db.copy");
            var copyWal = copy + "-wal";
            var copyShm = copy + "-shm";
            File.WriteAllBytes(copyWal, new byte[] { 0x37, 0x7f, 0x06, 0x82 });
            File.WriteAllBytes(copyShm, new byte[] { 0x01 });

            var aside = new SqliteCipherHelper.ReinitAside(
                copy, SqliteCipherHelper.AsideContent.Plaintext, new[] { copyWal, copyShm });

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            try
            {
                SqliteCipherHelper.RemoveVerifiedPlaintextAside(
                    $"Data Source={store}", aside, Verified, CarriedNothing);
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }

            Assert.False(File.Exists(copy));
            Assert.False(File.Exists(copyWal));
            Assert.False(File.Exists(copyShm));

            // And the line says so, rather than reporting a delete that was only partly done.
            var all = captured.Snapshot(Serilog.Events.LogEventLevel.Verbose);
            Assert.Contains(all, m => m.Contains("and all 2 unencrypted sibling file(s) beside it"));
        }

        // ──────────────────────────────────────────────────────────────────
        //  The cache chain (Adrian's ruling, 2026-08-08, point 5)
        //
        //  liveQueriesCacheStore is an AddSingleton and a REQUIRED constructor dependency of eight
        //  other registrations, so it is built while the container resolves at startup — and its
        //  schema init was the one chain in this application with no try/catch anywhere in it.
        //  Since 2026-08-06 SqliteCipherHelper THROWS on a store file it cannot clear rather than
        //  reopening on top of it, so a held cache file took the whole container down; under the
        //  Windows SCM that is a restart loop.
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void A_held_cache_file_no_longer_propagates_out_of_the_cache_stores_constructor()
        {
            // The cache store resolves its own path off AppDomain.BaseDirectory and takes no
            // override, so this test works on the real file and puts back whatever it found.
            var cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SQLTriage-cache.db");
            var stash = cachePath + ".reinit-test-stash";

            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(cachePath + suffix)) File.Move(cachePath + suffix, stash + suffix, overwrite: true);
            }

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            try
            {
                // A PLAIN cache file — so the re-init path is entered at all — held in the share
                // mode a backup agent or a second copy of the app holds it in: SQLite can open it,
                // and Windows refuses the move and the delete because FILE_SHARE_DELETE is absent.
                using (var seed = new SqliteConnection($"Data Source={cachePath}"))
                {
                    seed.Open();
                    using var cmd = seed.CreateCommand();
                    cmd.CommandText = "CREATE TABLE held (id INTEGER);";
                    cmd.ExecuteNonQuery();
                }
                SqliteConnection.ClearAllPools();
                Assert.Equal(PlainHeader, Head(cachePath, PlainHeader.Length));

                using var hold = new FileStream(
                    cachePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

                Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                    .MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();

                // NO ASSERTION WRAPPER: a throw here fails the test, and that IS the assertion.
                // Before this fix the IOException from the re-init travelled straight out of the
                // constructor and, at startup, out of the container.
                var store = new SQLTriage.Data.Caching.liveQueriesCacheStore();
                store.Dispose();

                Serilog.Log.Logger = previousLogger;

                // Degrading quietly would be its own defect. The operator is told what stopped
                // working, at Error.
                var errors = captured.Snapshot(Serilog.Events.LogEventLevel.Error);
                Assert.Contains(errors, m =>
                    m.Contains("Could not initialise the dashboard cache store")
                    && m.Contains("cache is NOT available for this run"));
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
                SqliteConnection.ClearAllPools();
                foreach (var leftover in Directory.GetFiles(
                    AppDomain.CurrentDomain.BaseDirectory, "SQLTriage-cache.db*"))
                {
                    if (leftover.EndsWith(".reinit-test-stash", StringComparison.Ordinal)) continue;
                    if (leftover.Contains(".reinit-test-stash", StringComparison.Ordinal)) continue;
                    try { File.Delete(leftover); } catch { /* teardown */ }
                }
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    if (File.Exists(stash + suffix)) File.Move(stash + suffix, cachePath + suffix, overwrite: true);
                }
            }
        }

        /// <summary>
        /// Minimal in-memory Serilog sink, so "the operator is told" is exercised rather than
        /// asserted in a comment. Same shape as the one in AuditLogServiceTests; xunit.runner.json
        /// pins this suite to one thread, so swapping the global logger is safe.
        /// </summary>
        private sealed class CapturingSink : Serilog.Core.ILogEventSink
        {
            private readonly System.Collections.Generic.List<(Serilog.Events.LogEventLevel Level, string Message)> _events = new();

            public void Emit(Serilog.Events.LogEvent logEvent)
            {
                lock (_events)
                    _events.Add((logEvent.Level, logEvent.RenderMessage()));
            }

            public System.Collections.Generic.List<string> Snapshot(Serilog.Events.LogEventLevel minimumLevel)
            {
                lock (_events)
                    return _events.Where(e => e.Level >= minimumLevel).Select(e => e.Message).ToList();
            }
        }
    }
}
