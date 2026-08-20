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
    /// Migration: if the target DB file exists but the key does not open it (detected by
    /// running a probe query after applying the key), the file has to be replaced. What happens
    /// to its CONTENTS depends on what the file turns out to be:
    ///
    ///   PLAINTEXT — an unencrypted database, the ordinary plain→encrypted migration. Its
    ///     contents are EXPORTED into the replacement (ATTACH + <c>sqlcipher_export</c>; see
    ///     <see cref="TryExportPlaintextStore"/>), so nothing is lost. Adrian's ruling, 2026-08-08.
    ///
    ///   ANYTHING ELSE — SQLCipher under a key nobody has, corrupt, or too short to classify.
    ///     Nothing here can read it, so nothing here can export it. What happens then is the
    ///     per-store FALLBACK POLICY (<see cref="FallbackByStore"/>): the stores holding
    ///     operator-entered data REFUSE rather than hand back an empty store, and the caches
    ///     re-initialise empty and say so. An unlisted store refuses.
    ///
    /// ⚠ Both of those replaced a path that ALWAYS handed back an empty store, and the sentence
    /// that used to stand here — "all SQLite stores are regenerable caches, no master data is
    /// lost" — was false. It was contradicted 200 lines below by the H2 note in
    /// <see cref="IsKeyValid"/>, which names the stores that hold operator-entered data nothing
    /// regenerates: check-baselines.db (the F6 accepted-findings an operator has ticked off, one
    /// at a time, by hand) and the config baselines. Most of the other stores really are caches;
    /// those are not, and the claim has to be written for the worst of them.
    ///
    /// That is why the old file is renamed aside rather than deleted outright, and why the one
    /// case where the aside IS deleted (see <see cref="RemoveVerifiedPlaintextAside"/>) is
    /// conditional on first PROVING the replacement store opens and reads back — and on measuring
    /// that the replacement is itself not a plain file, since "the copy was the readable one" is
    /// the entire justification for destroying it.
    ///
    /// The aside is the rollback source for every failure up to the moment the replacement is
    /// PROVEN, and it has to be COMPLETE — which is why the -wal/-shm/-journal siblings now travel
    /// WITH it instead of being deleted.
    ///
    /// ⚠ IT IS NOT KEPT UNCONDITIONALLY, and the sentence that stood here until 2026-08-08 — "a
    /// failure ANYWHERE leaves it" — was a false universal, measured false on this path: an
    /// EMPTY-OK store whose file classifies as PLAINTEXT and whose export then FAILS is renamed
    /// aside, re-initialised empty, and the plaintext aside IS deleted, because the delete gate
    /// (<see cref="RemoveVerifiedPlaintextAside"/>) turns on the copy being readable-by-anyone and
    /// on the replacement working — not on the export having carried anything. That is the ruling's
    /// own answer for the stores that refill from elsewhere (point 3), and the delete line says in
    /// as many words that the contents are gone with it. The failures that DO leave the aside are
    /// the ones that never reach the gate: a clear that could not finish, an install that could not
    /// finish, a replacement that did not read back, a replacement not measured as encrypted — plus
    /// the whole UNREADABLE population, which nothing on this path deletes at any point.
    ///
    /// Probe (h) measures a WAL-mode store whose entire schema and all 5000 of its rows lived in
    /// the -wal, with the main file alone reading back <c>no such table</c>. ⚠ Probe (i) then
    /// measures why the old delete usually got away with it: this class's own key probe checkpoints
    /// such a store on the way past, so by the time the rename runs there is normally no -wal left
    /// to delete. The move is for when that does not happen — a contended file, which is the case
    /// this whole path exists for. See <see cref="MoveSidecarsBeside"/>.
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
        /// Applies PRAGMA key immediately after open. An existing file the key does not open is
        /// renamed aside and replaced: a PLAIN file's contents are exported into the replacement,
        /// and a file nothing here can read falls back to this store's entry in
        /// <see cref="FallbackByStore"/> — refuse, or re-initialise empty and say so. See the
        /// migration note on the class. Caller must dispose the returned connection.
        /// </summary>
        public static SqliteConnection OpenEncrypted(string connectionString)
        {
            var hexKey = GetOrCreateHexKey();
            var conn = new SqliteConnection(connectionString);
            conn.Open();

            using (var keyCmd = conn.CreateCommand())
            {
                keyCmd.CommandText = $"PRAGMA key = \"x'{hexKey}'\";";
                keyCmd.ExecuteNonQuery();
            }

            try
            {
                if (!IsKeyValid(conn, connectionString))
                {
                    // ⚠ THIS DISPOSE IS THE FIX, not tidiness (found 2026-08-06 by a positive
                    // control on the re-init path). The re-init RENAMES the old file aside, and
                    // this connection is OPEN ON THAT FILE. Windows refuses a move or a delete of
                    // a file with a live handle, and SQLite does not open with FILE_SHARE_DELETE,
                    // so the rename and its delete fallback BOTH failed — every time, on every
                    // install. The old code swallowed both and reopened anyway, so the H2
                    // "preserve the unreadable file aside" defence never once ran, and the failure
                    // it was silently absorbing was one the helper was causing itself.
                    conn.Dispose();
                    return ReopenAfterMigration(connectionString, hexKey);
                }
            }
            catch
            {
                // IsKeyValid now rethrows on transient errors (H2). Dispose the open connection
                // before unwinding so a busy/locked/full probe does not leak a native handle,
                // file lock, and pool slot on every retry.
                conn.Dispose();
                throw;
            }

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

            using (var keyCmd = conn.CreateCommand())
            {
                keyCmd.CommandText = $"PRAGMA key = \"x'{hexKey}'\";";
                await keyCmd.ExecuteNonQueryAsync();
            }

            try
            {
                if (!IsKeyValid(conn, connectionString))
                {
                    // See OpenEncrypted: the re-init renames the file this connection has open,
                    // and Windows will not move a file with a live handle on it.
                    await conn.DisposeAsync();
                    return await ReopenAfterMigrationAsync(connectionString, hexKey);
                }
            }
            catch
            {
                // See OpenEncrypted: dispose before unwinding so a transient probe error (H2
                // rethrow) does not leak the open connection.
                await conn.DisposeAsync();
                throw;
            }

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
            var regenerating = false;

            if (File.Exists(KeyFilePath))
            {
                try
                {
                    var protected_ = File.ReadAllBytes(KeyFilePath);
                    rawKey = ProtectedData.Unprotect(protected_, AppEntropy, DataProtectionScope.LocalMachine);
                    if (rawKey.Length == 32)
                        return Convert.ToHexString(rawKey);

                    // RULED 2026-08-10: this material UNWRAPPED. It is the wrong length for a
                    // SQLCipher key so it cannot be used, but it is READABLE key material, and a
                    // store encrypted under it has no other route back. Until this ruling the branch
                    // regenerated straight over it with no aside at all, which destroyed the one
                    // thing on the disk that could still open those stores. It now takes the same
                    // aside as material that will not unwrap, and the same refusal if that aside
                    // cannot be taken.
                    Serilog.Log.Error(
                        "[SqliteCipherHelper] The SQLite cipher key at {Path} unwrapped to {Len} bytes rather "
                        + "than 32, so it cannot be used as a key; preserving it aside and regenerating",
                        KeyFilePath, rawKey.Length);
                    RefuseUnlessPreserved(KeyAsideLifecycle.SetAside(KeyFilePath, AsideLog));
                    regenerating = true;
                }
                catch (CryptographicException ex)
                {
                    // MED (2026-07-07): the key file EXISTS but won't unwrap. Overwriting it in place
                    // orphans every encrypted store irrecoverably. Preserve it aside first so a
                    // transient DPAPI fault (backup restore, SID change) stays recoverable, then
                    // generate a fresh key. Log at Error — this is data-affecting, not routine.
                    Serilog.Log.Error(ex, "[SqliteCipherHelper] Could not unwrap SQLite cipher key at {Path}; preserving aside and regenerating (encrypted stores will re-initialise)", KeyFilePath);
                    RefuseUnlessPreserved(KeyAsideLifecycle.SetAside(KeyFilePath, AsideLog));
                    regenerating = true;
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

            // The KEY aside's lifecycle (HOUSE RULE 2026-08-09), which is a different ruling from
            // the .pre-reinit- DATABASE aside further down this file: see KeyAsideLifecycle's class
            // doc for why the two have OPPOSITE polarity on an unreadable file. Runs only where a
            // key was actually replaced, and the cleanup is authorised by the proof and nothing else.
            if (regenerating)
            {
                var proof = KeyAsideLifecycle.ProveReplacement(
                    KeyFilePath, rawKey, AppEntropy, DataProtectionScope.LocalMachine);

                // The reconcile runs BEFORE the refusal on purpose. It deletes nothing while the
                // proof is unproven (it is handed the same proof), and what it does do is name every
                // preserved file in the log, which is exactly what the refusal below tells the
                // operator to look for.
                KeyAsideLifecycle.ReconcileAsides(
                    KeyFilePath, rawKey, AppEntropy, DataProtectionScope.LocalMachine, proof, AsideLog);

                // RULED 2026-08-10: a key that cannot be proven readable back off disk does not
                // become the key in force. Until this ruling the failure was logged at Error and the
                // unproven key was used anyway, which is the state ProveReplacement exists to catch:
                // every store written under it would then be unopenable from the next start on.
                if (!proof.Proven)
                {
                    var message = KeyAsideLifecycle.RefusalNotProven(
                        KeyFilePath,
                        "a cipher key the next start cannot read would leave every store written "
                        + "under it unopenable, and nothing would say so until that restart",
                        proof.Detail);
                    Serilog.Log.Error("[SqliteCipherHelper] {Message}", message);
                    throw new KeyAsideRefusedException(message);
                }
            }

            return Convert.ToHexString(rawKey);
        }

        /// <summary>
        /// Posture (b), RULED 2026-08-10: this class does not write a fresh cipher key over material
        /// it could not preserve. The refusal is per attempt and caches nothing, so a file that was
        /// briefly locked is retried on the next call.
        /// </summary>
        private static void RefuseUnlessPreserved(KeyAsideResult aside)
        {
            if (aside.SafeToOverwrite) return;

            var message = KeyAsideLifecycle.RefusalNotPreserved(
                KeyFilePath,
                "every store already encrypted under it could never be opened again",
                aside);
            Serilog.Log.Error("[SqliteCipherHelper] {Message}", message);
            throw new KeyAsideRefusedException(message);
        }

        /// <summary>Routes <see cref="KeyAsideLifecycle"/>'s sentences into this class's log prefix.</summary>
        private static void AsideLog(bool isError, Exception? error, string message)
        {
            if (isError) Serilog.Log.Error(error, "[SqliteCipherHelper] {Message}", message);
            else Serilog.Log.Warning(error, "[SqliteCipherHelper] {Message}", message);
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
                // H2 (2026-07-07): only a genuinely plain / wrong-key / corrupt file may trigger the
                // delete-and-recreate path. A transient or environmental fault (busy, locked, disk
                // full, I/O, read-only) must NOT be misread as "unencrypted" — that path destroys
                // check-baselines.db (F6 operator accepted-findings) and the config baselines.
                // Rethrow those so the caller surfaces the real error and the data is left intact.
                switch (ex.SqliteErrorCode)
                {
                    case 5:   // SQLITE_BUSY
                    case 6:   // SQLITE_LOCKED
                    case 7:   // SQLITE_NOMEM
                    case 8:   // SQLITE_READONLY
                    case 10:  // SQLITE_IOERR
                    case 13:  // SQLITE_FULL
                    case 14:  // SQLITE_CANTOPEN
                        Serilog.Log.Error(ex, "[SqliteCipherHelper] Transient/environmental error probing {ConnStr} (code {Code}) — leaving data intact, NOT re-initialising", connectionString, ex.SqliteErrorCode);
                        throw;
                    default:  // NOTADB (26), CORRUPT (11), malformed schema (1) — genuinely unusable
                        // ⚠ THE LINE STOPS AT THE CLASSIFICATION. It used to end "; re-initialising",
                        // which this function is in no position to say: what happens to the file is
                        // the caller's decision, and since the fallback registry (2026-08-08) the
                        // answer for the REFUSE population is that it will NOT be re-initialised.
                        // Measured on 2026-08-08 in a seat-register.db refusal, where the captured
                        // log read, consecutively, "…re-initialising" and then "SQLTriage will not
                        // re-initialise the encrypted store 'seat-register.db'". Same correction as
                        // the one already applied to ReadFileHead's warnings, and for the same
                        // reason: a measurement may not carry a consequence its caller decides.
                        Serilog.Log.Warning(ex, "[SqliteCipherHelper] Probe failed for {ConnStr} (code {Code}) — plain, keyed differently, or corrupt. What becomes of the file is decided by the caller and reported on the lines below", connectionString, ex.SqliteErrorCode);
                        return false;
                }
            }
        }

        private static SqliteConnection ReopenAfterMigration(string connectionString, string hexKey)
        {
            // STEP 1, and it happens before anything on disk is renamed or removed: build the
            // replacement OFF TO ONE SIDE, out of the existing file's contents if they can be
            // read. Throws the policy refusal for a store that may not come back empty.
            var export = PrepareReplacementStore(connectionString);

            if (!TryClearDbFile(connectionString, out var blocking, out var aside))
            {
                // A refusal that renamed something first still owes the reader a sentence about it.
                DiscardExportTemp(export);
                ReportAsideSurvivedFailure(aside);
                throw ReinitBlocked(blocking);
            }

            if (!TryInstallExportedStore(connectionString, ref export))
            {
                ReportAsideSurvivedFailure(aside);
                throw ExportInstallFailed(connectionString, export);
            }

            var conn = new SqliteConnection(connectionString);
            try
            {
                conn.Open();
                using (var keyCmd = conn.CreateCommand())
                {
                    keyCmd.CommandText = $"PRAGMA key = \"x'{hexKey}'\";";
                    keyCmd.ExecuteNonQuery();
                }
                CommitEncryption(conn);
                // ORDER IS THE WHOLE POINT (Adrian's ruling, 2026-08-06), which is why the
                // read-back, the report and the deletion are ONE call with the order written down
                // inside it rather than three statements a future edit can shuffle here. Nothing
                // irreversible happens to the copy until the replacement has been proven to work.
                CompleteReinitialisation(conn, connectionString, aside, export);
                return conn;
            }
            catch
            {
                // Dispose the connection we just created if re-init fails mid-way, so a
                // throwing Open/PRAGMA/CommitEncryption cannot leak it (adversarial review 2026-07-07).
                conn.Dispose();
                ReportAsideSurvivedFailure(aside);
                throw;
            }
        }

        private static async Task<SqliteConnection> ReopenAfterMigrationAsync(string connectionString, string hexKey)
        {
            // Synchronous deliberately: the export is one ATTACH, one scalar and one DETACH on a
            // connection nothing else can see, and an async twin of it would be a second ordering
            // to keep in step with this one. See the note on CompleteReinitialisation.
            var export = PrepareReplacementStore(connectionString);

            if (!TryClearDbFile(connectionString, out var blocking, out var aside))
            {
                // A refusal that renamed something first still owes the reader a sentence about it.
                DiscardExportTemp(export);
                ReportAsideSurvivedFailure(aside);
                throw ReinitBlocked(blocking);
            }

            if (!TryInstallExportedStore(connectionString, ref export))
            {
                ReportAsideSurvivedFailure(aside);
                throw ExportInstallFailed(connectionString, export);
            }

            var conn = new SqliteConnection(connectionString);
            try
            {
                await conn.OpenAsync();
                using (var keyCmd = conn.CreateCommand())
                {
                    keyCmd.CommandText = $"PRAGMA key = \"x'{hexKey}'\";";
                    await keyCmd.ExecuteNonQueryAsync();
                }
                CommitEncryption(conn);
                // The same one call as the sync path, and it being one call is what makes "the
                // async half drifted out of step with the sync half" — a shape this tree has
                // shipped before — a diff of a single line rather than of an ordering.
                CompleteReinitialisation(conn, connectionString, aside, export);
                return conn;
            }
            catch
            {
                await conn.DisposeAsync();
                ReportAsideSurvivedFailure(aside);
                throw;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        //  EXPORT-FIRST (Adrian's ruling, 2026-08-08, point 1)
        //
        //  ── THE PROBE RESULTS THIS IS BUILT ON ──────────────────────────
        //  Measured 2026-08-08 by a throwaway harness against the native library this project
        //  actually vendors — SQLitePCLRaw.bundle_e_sqlcipher 2.1.10, which reported
        //  `PRAGMA cipher_version` = 4.5.2 community over SQLite 3.39.2. These are measurements,
        //  not documentation, and they are written down because three of them are load-bearing
        //  and one of them is a silent trap.
        //
        //  (a) DIRECTION. Plain source as `main`, encrypted destination ATTACHed, then
        //      `SELECT sqlcipher_export('<attached>')` — works. The reverse (encrypted `main`,
        //      plain source ATTACHed with `KEY ''`, `sqlcipher_export('main','plain')`) also
        //      works. The first is used: the source is the file we already have.
        //
        //  (b) ⚠⚠ THE KEY FORM IS A SILENT TRAP. Four ATTACH KEY spellings were tried and ALL
        //      FOUR exported without an error. Only two produced a store that
        //      `PRAGMA key = "x'HEX'"` — the form every other line in this file uses — can open:
        //        KEY "x'HEX'"           (TEXT whose content is x'HEX')   → readable  ✔
        //        KEY $k, $k = "x'HEX'"  (the same TEXT, parameterised)   → readable  ✔
        //        KEY x'HEX'             (a BLOB literal)                 → SQLite Error 26,
        //                                                                  'file is not a database'
        //        KEY 'HEX'              (bare hex as a passphrase)       → SQLite Error 26
        //      A wrong key here is not an exception. It is an export that reports success and
        //      leaves a store nothing can ever open again. That is why the export below reads its
        //      own output back THROUGH THE PRODUCTION KEY before it is allowed to count.
        //
        //  (c) QUOTING. The parameterised form is used, so there is no literal to escape. Also
        //      measured, for whoever is tempted to interpolate: an un-doubled single quote in the
        //      path literal throws SQLite Error 1 at parse (loud), and doubling it works.
        //
        //  (d) ⚠⚠ A KILLED EXPORT IS INDISTINGUISHABLE FROM A HEALTHY STORE. A child process was
        //      hard-killed (TerminateProcess) ~120 ms into exporting a 2,000,000-row source. What
        //      it left: an 8 MB destination whose first 16 bytes are NOT the plain header, plus a
        //      hot 9,728-byte -journal. Run IsKeyValid's exact probe sequence against it and the
        //      read probe returns OK and the commit probe returns OK — IsKeyValid says VALID —
        //      while `SELECT count(*)` over the exported table returns ZERO (the hot journal
        //      rolled the row copy back and left the schema committed). So an interrupted export
        //      written straight to the store path becomes, on the next start, a permanently empty
        //      store that nothing re-initialises and nothing complains about.
        //      THAT is why the export goes to a temp file that is verified and only then moved
        //      into place, and why the original is not renamed until the temp has been verified.
        //
        //  (e) Exporting into a destination that already holds an unrelated table (_sqlcipher_init)
        //      works. Exporting into one that already holds the SAME tables throws SQLite Error 1,
        //      'table X already exists' — so a leftover temp is deleted before the export starts.
        //
        //  (f) CARRIED: tables, indexes (including UNIQUE), triggers, views, sqlite_sequence and
        //      row values — measured across a source holding every one of them. NOT carried:
        //      `PRAGMA user_version` (source 77 → destination 0). No code in this tree reads
        //      user_version (grep over *.cs and *.razor, 2026-08-08), so nothing here depends on it.
        //
        //  (g) Exporting from a `Mode=ReadOnly` source connection throws SQLite Error 14: the
        //      attached destination inherits the read-only flag and cannot be created.
        //
        //  (h) WAL. A plain source in WAL mode with an uncheckpointed WAL had its ENTIRE schema
        //      and all 5000 of its rows in the -wal: a copy of the main file alone read back
        //      `no such table`, a copy of main + -wal read 5000, and the export (which opens the
        //      source normally and therefore reads through the WAL) carried all 5000.
        //
        //  (i) ⚠ THE KEY PROBE ITSELF REWRITES THE FILE. Running this class's own probe sequence
        //      against a hot-WAL store and then closing the connection CHECKPOINTED the WAL into
        //      the main file and deleted the -wal — for a plain store and for one encrypted under a
        //      foreign key, and after the probe had already failed with SQLITE_NOTADB. The main
        //      file grew 4096 → 20480 bytes and read back all 500 rows under its RIGHTFUL key, so
        //      the checkpoint copies the frames faithfully rather than corrupting them. Two
        //      consequences: "the copy is byte-for-byte untouched" is FALSE for a hot-WAL store,
        //      and the aside is usually complete by checkpoint rather than by preservation. See
        //      MoveSidecarsBeside for the case where the checkpoint does not happen.
        //
        //  (j) ⚠ LEAVING A SIBLING AT THE STORE'S OWN PATH DOES NOT PRESERVE IT, AND IT DOES NOT
        //      BREAK THE NEW STORE EITHER — both halves measured 2026-08-08, and both matter to
        //      RemoveStrandedSidecars. A 37,112-byte hot -wal holding all 500 rows was left at the
        //      store path with the main file gone, and the re-init's own next steps were run there
        //      (open, PRAGMA key, CREATE TABLE _sqlcipher_init, read back). The new encrypted store
        //      opened cleanly and held exactly its own marker — sqlite_master = 1 row — and on
        //      close the stale -wal WAS GONE: SQLite discarded it rather than adopting it or
        //      choking on it. Measured for a -wal written by a plain store and for one written
        //      under a foreign key. So (1) a stranded sibling is not a hazard to the replacement,
        //      which is why not deleting it is safe, and (2) not deleting it is not the same as
        //      saving it — the next open at that path removes it. The only thing that actually
        //      preserves those pages is getting a copy of them beside the aside, which is what the
        //      copy fallback in MoveSidecarsBeside is for; the log lines say the rest.
        //      Recovery measured in the same run: the aside alone read `no such table: accepted`,
        //      and the aside with its -wal renamed beside it read 500.
        //
        //  (k) File.Move FAILS and File.Copy SUCCEEDS on a file another handle holds with
        //      FileShare.Read (measured 2026-08-08: Move → IOException "used by another process",
        //      Copy → succeeded, 4 of 4 bytes; File.Delete also IOException). A rename needs
        //      FILE_SHARE_DELETE from every open handle; a read does not. That asymmetry is the
        //      whole basis of the copy fallback below — the contended sibling this path exists for
        //      is exactly the one that can still be read.
        // ──────────────────────────────────────────────────────────────────

        /// <summary>What became of the attempt to carry the old store's contents into the new one.</summary>
        internal enum ExportOutcome
        {
            /// <summary>Not tried: there was no readable plaintext source to export FROM.</summary>
            NotAttempted,

            /// <summary>Tried and failed. <see cref="StoreExport.Detail"/> says how.</summary>
            Failed,

            /// <summary>
            /// Exported AND verified: the temp store was read back through the production key and
            /// its schema and per-table row counts matched the source's exactly.
            /// </summary>
            Exported,
        }

        /// <summary>
        /// The export's own report on itself — what it did, where it put it, and how much it
        /// measured going across.
        ///
        /// <para>The counts are here so the log lines and the aside's fate can be conditioned on
        /// the same measurement that authorised them, rather than on the fact that an export
        /// function was called. <see cref="ExportOutcome.Exported"/> is false in a
        /// <c>default</c> value, which is the answer that carries nothing and claims nothing.</para>
        /// </summary>
        internal readonly record struct StoreExport(
            ExportOutcome Outcome, string? TempPath, long Objects, long Rows, string? Detail)
        {
            internal bool Carried => Outcome == ExportOutcome.Exported;
        }

        /// <summary>
        /// What a store gets when its contents CANNOT be carried across — because the existing
        /// file is not readable by anyone here, or because the export failed.
        /// </summary>
        internal enum ReinitFallback
        {
            /// <summary>
            /// Do not hand back an empty store. Throw, leave the existing file exactly where it
            /// is, and say so loudly. Every consumer of a store on this list already wraps its
            /// schema init in try/catch (verified by reading all five, 2026-08-08), so the cost is
            /// a broken feature that logs, not a service that will not start.
            /// </summary>
            Refuse,

            /// <summary>
            /// Re-initialise empty — the behaviour every store had before 2026-08-08 — and
            /// announce it. For these stores the contents refill from somewhere else.
            /// </summary>
            EmptyOk,
        }

        /// <summary>
        /// THE POLICY REGISTRY (Adrian's ruling, 2026-08-08, point 3). One place, keyed on the
        /// store's FILE NAME, with the evidence for each verdict beside it.
        ///
        /// <para>⚠ THE DEFAULT FOR AN UNLISTED NAME IS <see cref="ReinitFallback.Refuse"/> —
        /// see <see cref="FallbackPolicyFor"/>. A store nobody has classified is a store nobody
        /// has established is a cache, and the failure this whole file exists to prevent is
        /// handing an operator an empty database that used to hold their work.</para>
        ///
        /// <para>Every entry is an ASSERTION about what the store holds, so every entry says what
        /// it holds and where that came from, not just a verdict. The population is the fourteen
        /// distinct <c>.db</c> names this assembly opens through <see cref="OpenEncrypted"/>
        /// (enumerated 2026-08-08 from the Path.Combine call sites under Data/).</para>
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, ReinitFallback> FallbackByStore =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // ── REFUSE: operator-entered or commercially load-bearing, nothing regenerates it ──

                // AcceptedFindingsService, Data/check-baselines.db. The F6 accepted findings: a
                // DBA ticks these off one at a time, by hand, and no scan re-derives them. This is
                // the store the whole aside ruling was written for.
                ["check-baselines.db"] = ReinitFallback.Refuse,

                // ServerConfigBaselineService, Data/server-config-baselines.db. The approved
                // configuration an instance is compared AGAINST. An empty one does not report
                // "no baseline" to a reader glancing at a drift page — it reports no drift.
                ["server-config-baselines.db"] = ReinitFallback.Refuse,

                // ChangeItemService, Data/change-items.db. Change-control items: what was
                // proposed, what was approved, what was applied. Written by people, not collected
                // from a server, so there is nowhere to collect it from again.
                ["change-items.db"] = ReinitFallback.Refuse,

                // AlertHistoryService, alert-history.db. Fire / acknowledge / resolve history,
                // including the acknowledgements an operator made. The events are in the past; a
                // re-collection cannot reach them.
                ["alert-history.db"] = ReinitFallback.Refuse,

                // SeatRegister, Data/seat-register.db. Licence seat claims and releases, append-only
                // by design. Losing it is a commercial fact about who is licensed, not a cache miss.
                ["seat-register.db"] = ReinitFallback.Refuse,

                // ── EMPTY-OK: refills from the monitored instances or from a recomputation ──

                // GovernanceHistoryService (also HistoricalPerformanceService,
                // PerformanceBaselineService and WaitStatsHistoryService, which all open this same
                // file). Sampled telemetry off the monitored SQL Servers; the collectors refill it.
                ["governance-history.db"] = ReinitFallback.EmptyOk,

                // ChangedObjectsService, Data/changed-objects.db. Snapshots of schema objects,
                // re-derived by the next scan of the same instances.
                ["changed-objects.db"] = ReinitFallback.EmptyOk,

                // BlockingHistoryService, blocking-history.db. Sampled blocking events on a 30-day
                // retention — the store is already designed to lose its own tail.
                ["blocking-history.db"] = ReinitFallback.EmptyOk,

                // liveQueriesTableService, SQLTriage.db. Tables built from SQL Server query
                // results for the dashboards; rebuilt from the queries.
                ["SQLTriage.db"] = ReinitFallback.EmptyOk,

                // CodeHotspotsCacheService, code-hotspots-cache.db. A cache of an analysis, by
                // name and by construction; recomputed on demand.
                ["code-hotspots-cache.db"] = ReinitFallback.EmptyOk,

                // ScheduledTaskHistoryService, scheduled-task-history.db. Run history of the app's
                // own scheduled tasks on a 90-day retention. ⚠ This is a LOG, not a cache: an
                // empty one loses runs that nothing re-derives. It is on this list because the
                // ruling put it here, and the cost is a gap in a history page, not lost operator
                // input.
                ["scheduled-task-history.db"] = ReinitFallback.EmptyOk,

                // ConsolidationHistoryStore, consolidation-history.db. Capacity samples the
                // collector refills from the monitored instances.
                ["consolidation-history.db"] = ReinitFallback.EmptyOk,

                // UptimeTrackerService, Data/Caching/uptime.db. Session-start and heartbeat events.
                // ⚠ An empty one UNDERSTATES availability over any window it used to cover — the
                // same failure the 2026-08-05 note in that service is about — but the events
                // resume on the next start.
                ["uptime.db"] = ReinitFallback.EmptyOk,

                // liveQueriesCacheStore, SQLTriage-cache.db. Dashboard query results. The one
                // store in the population that is a cache in the strict sense: every row has a
                // fetched_at and an eviction policy already deletes it.
                ["SQLTriage-cache.db"] = ReinitFallback.EmptyOk,
            };

        /// <summary>
        /// Every store name the registry rules on. Exposed so the census test can compare it
        /// against the store names it finds in the SOURCE TREE rather than against a second
        /// hand-written list — the theory that used to guard this enumerated fourteen names in its
        /// own [InlineData] and therefore could not fail when a FIFTEENTH store was added, which is
        /// the direction that puts an unruled store in front of a client.
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests).</para>
        /// </summary>
        internal static System.Collections.Generic.IReadOnlyCollection<string> RuledStoreNames =>
            FallbackByStore.Keys;

        /// <summary>
        /// The fallback for the store at <paramref name="storePath"/>, keyed on its file name.
        /// An unlisted name REFUSES — see <see cref="FallbackByStore"/>.
        ///
        /// <para>A null path is the one case that is not a refusal: there is no file, so there is
        /// no operator data at stake and nothing to preserve. That is a measurement of the
        /// connection string, not a judgement about a store.</para>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests): the registry is the ruling, and a
        /// registry nothing asserts against is a comment.</para>
        /// </summary>
        internal static ReinitFallback FallbackPolicyFor(string? storePath)
        {
            if (storePath is null) return ReinitFallback.EmptyOk;
            var name = Path.GetFileName(storePath);
            return FallbackByStore.TryGetValue(name, out var policy) ? policy : ReinitFallback.Refuse;
        }

        /// <summary>
        /// Builds the replacement store off to one side, BEFORE anything on disk is renamed or
        /// removed, and throws this store's policy refusal if it cannot be built.
        ///
        /// <para>The ordering is the point. Until this returns, the existing file is untouched at
        /// its own path — so a refusal here costs the operator nothing at all, not even a renamed
        /// file to put back, and a crash here costs them nothing either.</para>
        /// </summary>
        private static StoreExport PrepareReplacementStore(string connectionString)
        {
            // The caller's own connection has just been disposed, which returns it to the POOL and
            // leaves the OS handle on the operator's file open. A refusal must not walk away with a
            // handle still on a file it is telling the operator to go and look at, and the export
            // below has to open the same file itself.
            SqliteConnection.ClearAllPools();

            var path = TryGetDataSourcePath(connectionString);
            var policy = FallbackPolicyFor(path);

            StoreExport export;
            if (path is null)
            {
                export = new StoreExport(ExportOutcome.NotAttempted, null, 0, 0,
                    "the connection string names no file on disk, so there was nothing to export from");
            }
            else
            {
                var reading = ReadFileHead(path);
                export = reading == FileHeadReading.PlainSqliteHeader
                    ? TryExportPlaintextStore(path)
                    : new StoreExport(ExportOutcome.NotAttempted, null, 0, 0, DescribeUnexportableSource(reading));
            }

            if (!export.Carried && policy == ReinitFallback.Refuse)
                throw ReinitRefused(connectionString, path, export);

            return export;
        }

        /// <summary>
        /// Why an existing store's contents could not even be attempted, worded for the SOURCE
        /// file. Separate from <see cref="DescribeHead"/>, which says the same three things about
        /// the REPLACEMENT store and draws the opposite conclusion from them — the two sentences
        /// were briefly one, and a shared clause that ends "so the replacement is not encrypted
        /// either" is nonsense pointed at a source file.
        /// </summary>
        private static string DescribeUnexportableSource(FileHeadReading reading) => reading switch
        {
            FileHeadReading.OtherBytes =>
                "the existing file's first 16 bytes were read and are not the plain SQLite header, so it is "
                + "encrypted under a key this install does not have (or corrupt) and nothing here can read it to "
                + "export it",
            FileHeadReading.TooShort =>
                "the existing file is shorter than the 16 bytes a database header needs, so nothing was measured "
                + "about its contents and there is nothing an export could safely read",
            _ =>
                "the existing file's first bytes could not be read at all, so nothing whatever is known about its "
                + "contents and an export was not attempted",
        };

        /// <summary>
        /// Copies a PLAIN database's contents into a fresh encrypted store beside it, and returns
        /// what it measured going across. Never throws: a failure is an outcome the caller's
        /// policy decides about.
        ///
        /// <para>Writes to a TEMP path, not to the store path — see probe (d) above, which
        /// measured a killed export leaving a destination that IsKeyValid calls valid and that
        /// holds zero rows. The caller moves the temp into place only after this has verified it.</para>
        ///
        /// <para>THE VERIFICATION IS THE POINT, and it is a read-back through the production key
        /// path, because probe (b) measured two ATTACH KEY spellings that export "successfully"
        /// and leave a store <c>PRAGMA key = "x'HEX'"</c> cannot open. Schema and per-table row
        /// counts are fingerprinted on both sides and compared byte for byte; anything short of
        /// equality is a failure, and a failure deletes the temp.</para>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests).</para>
        /// </summary>
        internal static StoreExport TryExportPlaintextStore(string sourcePath)
        {
            var tempPath = sourcePath + ExportTempSuffix
                + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ExportTempExtension;
            try
            {
                // Probe (e): an export into a file that already holds the same tables throws
                // 'table X already exists'. A temp left by an earlier interrupted run is exactly
                // that file.
                DeleteFileAndSidecars(tempPath);
                DiscardAbandonedExports(sourcePath);

                // ⚠ The source must exist BEFORE the connection below opens, because that
                // connection is ReadWriteCreate and would otherwise create an empty database at
                // this path and then faithfully export nothing out of it. The head has already
                // been read by the caller, so this is a narrow re-check, not a first look.
                if (!File.Exists(sourcePath))
                {
                    return new StoreExport(ExportOutcome.Failed, null, 0, 0,
                        "the file disappeared between being classified and being exported");
                }

                // ⚠ ReadWriteCreate, and NOT ReadWrite — measured, and it cost a full test run to
                // find. The ATTACHed destination inherits the main connection's open flags, so
                // without CREATE the ATTACH of a file that does not exist yet throws SQLite Error
                // 14, 'unable to open database'. Probe (g) recorded the same failure for
                // Mode=ReadOnly and this is the same mechanism one notch along.
                using (var source = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
                {
                    source.Open();
                    using (var attach = source.CreateCommand())
                    {
                        // Parameterised, so there is no path literal to escape (probe (c)) and the
                        // key is the TEXT form x'HEX' that probe (b) measured as the only
                        // parameterised spelling the production PRAGMA can reopen.
                        attach.CommandText = "ATTACH DATABASE $target AS reinit_target KEY $key;";
                        attach.Parameters.AddWithValue("$target", tempPath);
                        attach.Parameters.AddWithValue("$key", $"x'{GetOrCreateHexKey()}'");
                        attach.ExecuteNonQuery();
                    }
                    using (var export = source.CreateCommand())
                    {
                        export.CommandText = "SELECT sqlcipher_export('reinit_target');";
                        export.ExecuteNonQuery();
                    }
                    using (var detach = source.CreateCommand())
                    {
                        detach.CommandText = "DETACH DATABASE reinit_target;";
                        detach.ExecuteNonQuery();
                    }
                }
                SqliteConnection.ClearAllPools();

                // The gate is a separate call, not the next twenty lines of this one. See
                // VerifyExportedCopy: inlined here, two mutations that deleted it outright passed
                // the entire suite.
                return VerifyExportedCopy(sourcePath, tempPath);
            }
            catch (Exception ex)
            {
                SqliteConnection.ClearAllPools();
                DeleteFileAndSidecars(tempPath);
                // ⚠ NOT "the source has NOT been touched", which is what this said until
                // 2026-08-08 and which probe (i) had already measured false: reading a store opens
                // it, and closing that connection can fold an uncheckpointed -wal into the main
                // file. What this run can actually assert is what it did with the file, not what
                // SQLite's recovery did to it on the way past.
                Serilog.Log.Error(ex,
                    "[SqliteCipherHelper] Could not export the contents of {Source} into a new encrypted store. Any partial export has been deleted, and nothing here renamed, moved or replaced the source — it is still at its own path under its own name",
                    sourcePath);
                return new StoreExport(ExportOutcome.Failed, null, 0, 0, ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Reads a finished export back and decides whether it may be used: the source and the copy
        /// are fingerprinted — every schema object with its DDL, every table with its row count —
        /// and anything short of exact equality deletes the copy and fails.
        ///
        /// <para>THE READ-BACK GOES THROUGH THE PRODUCTION KEY, and that is the point rather than a
        /// detail. Probe (b) measured two ATTACH KEY spellings that export without an error and
        /// leave a store <c>PRAGMA key = "x'HEX'"</c> cannot open at all. Nothing upstream of this
        /// notices: a wrong key is not an exception, it is a silently unreadable file.</para>
        ///
        /// <para>⚠ INTERNAL BECAUSE OF A SURVIVED MUTATION (2026-08-08). With this inline in
        /// <see cref="TryExportPlaintextStore"/>, two mutations passed the whole 58-test suite: one
        /// that made the comparison always succeed, and one that used the SOURCE's own fingerprint
        /// in place of reading the copy's. Each is the gate deleted outright, and nothing noticed —
        /// because every test drove an export that genuinely worked, so the gate never had to catch
        /// anything. There is no way in from outside: a wrong export cannot be provoked through
        /// OpenEncrypted. So it is driven directly, against a copy a test has made wrong on
        /// purpose.</para>
        /// </summary>
        internal static StoreExport VerifyExportedCopy(string sourcePath, string tempPath)
        {
            try
            {
                if (!File.Exists(sourcePath))
                {
                    DeleteFileAndSidecars(tempPath);
                    return new StoreExport(ExportOutcome.Failed, null, 0, 0,
                        "the source disappeared before its export could be checked against it");
                }

                string sourcePrint;
                long objects, rows;
                using (var source = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
                {
                    source.Open();
                    (sourcePrint, objects, rows) = ReadStoreFingerprint(source);
                }
                SqliteConnection.ClearAllPools();

                string targetPrint;
                long targetObjects, targetRows;
                using (var target = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = tempPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
                {
                    target.Open();
                    using (var key = target.CreateCommand())
                    {
                        key.CommandText = $"PRAGMA key = \"x'{GetOrCreateHexKey()}'\";";
                        key.ExecuteNonQuery();
                    }
                    (targetPrint, targetObjects, targetRows) = ReadStoreFingerprint(target);
                }
                SqliteConnection.ClearAllPools();

                if (!string.Equals(sourcePrint, targetPrint, StringComparison.Ordinal))
                {
                    DeleteFileAndSidecars(tempPath);
                    var detail =
                        "the export ran without error but what came back out of the new store does not match what "
                        + $"went in: the source holds {objects} schema object(s) and {rows} row(s), the export holds "
                        + $"{targetObjects} and {targetRows}";
                    Serilog.Log.Error(
                        "[SqliteCipherHelper] Refusing the export of {Source}: {Detail:l}. The copy has been deleted, and nothing here renamed, moved or replaced the source — it is still at its own path under its own name",
                        sourcePath, detail);
                    return new StoreExport(ExportOutcome.Failed, null, 0, 0, detail);
                }

                Serilog.Log.Information(
                    "[SqliteCipherHelper] Exported {Source} into a new encrypted store at {Temp}: {Objects} schema object(s) and {Rows} row(s), read back through the store key and measured equal to the source before the swap",
                    sourcePath, tempPath, objects, rows);
                return new StoreExport(ExportOutcome.Exported, tempPath, objects, rows, null);
            }
            catch (Exception ex)
            {
                SqliteConnection.ClearAllPools();
                DeleteFileAndSidecars(tempPath);
                Serilog.Log.Error(ex,
                    "[SqliteCipherHelper] Could not read the export of {Source} back to check it against the source, so it is not used. The copy has been deleted, and nothing here renamed, moved or replaced the source — it is still at its own path under its own name",
                    sourcePath);
                return new StoreExport(ExportOutcome.Failed, null, 0, 0, ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// A canonical rendering of everything a store holds that an export is supposed to carry:
        /// every schema object's type, name and DDL, and every table's row count.
        ///
        /// <para>Compared as a STRING rather than as two numbers on purpose. Object and row totals
        /// can match while the contents do not — two tables of five rows are not one table of ten
        /// — and this comparison is the only thing standing between a wrong-key export and a
        /// store that reports success.</para>
        ///
        /// <para>SQLite's own <c>sqlite_*</c> objects are excluded (with an explicit ESCAPE, so the
        /// <c>_</c> is a literal underscore rather than a wildcard); <c>sqlite_sequence</c> is one
        /// of them, and the export carries it whether or not this counts it.</para>
        /// </summary>
        /// <summary>
        /// ASCII unit/record separators, used to join the fingerprint's fields. Non-printing and
        /// not legal in an identifier or in SQL text, so no schema can spell one and merge two
        /// fields into a value that matches a different pair (a fingerprint joined with commas
        /// can be forged by a column named after one).
        /// </summary>
        private const char FieldSeparator = '\u001F';
        private const char RecordSeparator = '\u001E';

        private static (string Fingerprint, long Objects, long Rows) ReadStoreFingerprint(SqliteConnection conn)
        {
            var print = new System.Text.StringBuilder();
            var tables = new System.Collections.Generic.List<string>();
            long objects = 0;

            using (var schema = conn.CreateCommand())
            {
                schema.CommandText =
                    @"SELECT type, name, COALESCE(sql, '') FROM sqlite_master
                      WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\'
                      ORDER BY type, name;";
                using var reader = schema.ExecuteReader();
                while (reader.Read())
                {
                    objects++;
                    print.Append(reader.GetString(0)).Append(FieldSeparator)
                         .Append(reader.GetString(1)).Append(FieldSeparator)
                         .Append(reader.GetString(2)).Append(RecordSeparator);
                    if (reader.GetString(0) == "table") tables.Add(reader.GetString(1));
                }
            }

            long rows = 0;
            foreach (var table in tables)
            {
                using var count = conn.CreateCommand();
                count.CommandText = $"SELECT count(*) FROM \"{table.Replace("\"", "\"\"")}\";";
                var n = Convert.ToInt64(count.ExecuteScalar() ?? 0L);
                rows += n;
                print.Append(table).Append('=').Append(n).Append(RecordSeparator);
            }

            return (print.ToString(), objects, rows);
        }

        /// <summary>
        /// Moves a verified export into the store path, which <see cref="TryClearDbFile"/> has
        /// just cleared. Returns false — and downgrades the export so nothing downstream claims a
        /// carryover — if the move does not happen.
        ///
        /// <para>A failure here REFUSES for every store, listed or not, and that is deliberate. At
        /// this point the aside holds contents this run has already proven it can carry; falling
        /// back to an empty store would then run the plaintext deletion over an aside whose data
        /// went nowhere.</para>
        /// </summary>
        private static bool TryInstallExportedStore(string connectionString, ref StoreExport export)
        {
            if (!export.Carried || export.TempPath is not { } temp) return true;

            var path = TryGetDataSourcePath(connectionString);
            if (path is null)
            {
                DeleteFileAndSidecars(temp);
                export = new StoreExport(ExportOutcome.Failed, null, 0, 0,
                    "there is no file path in the connection string to move the export to");
                return false;
            }

            try
            {
                File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex,
                    "[SqliteCipherHelper] The export of {ConnStr} was built and verified but could not be moved into place at {Path}",
                    connectionString, path);
                DeleteFileAndSidecars(temp);
                export = new StoreExport(ExportOutcome.Failed, null, 0, 0,
                    "the verified export could not be moved into place: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Removes an export temp the run is not going to use, so a half-built store never outlives it.</summary>
        private static void DiscardExportTemp(StoreExport export)
        {
            if (export.TempPath is { } temp) DeleteFileAndSidecars(temp);
        }

        /// <summary>The shape of an export temp, used both to write one and to find abandoned ones.</summary>
        private const string ExportTempSuffix = ".reinit-";
        private const string ExportTempExtension = ".tmp";

        /// <summary>
        /// Deletes every export temp left beside this store by an earlier run.
        ///
        /// <para>THIS IS THE HALF-WRITTEN DESTINATION BEING REFUSED. Probe (d) measured what a
        /// process killed mid-export leaves behind: an encrypted file that is not a plain header,
        /// that IsKeyValid's read probe and commit probe both accept, and that holds ZERO rows
        /// because the hot journal rolled the row copy back and left the schema. Nothing about it
        /// looks wrong. It is never adopted — an export always writes a temp of its own with a
        /// fresh timestamp and this sweep removes the old ones — but "never adopted" is only true
        /// while it is also never LEFT, because a directory slowly filling with partial copies of a
        /// database is its own problem.</para>
        ///
        /// <para>The sweep is scoped to this store's own name plus the literal
        /// <c>.reinit-*.tmp</c> shape this class writes, so it can only ever delete files this
        /// class created.</para>
        /// </summary>
        private static void DiscardAbandonedExports(string sourcePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(sourcePath);
                if (string.IsNullOrEmpty(dir)) return;
                var pattern = Path.GetFileName(sourcePath) + ExportTempSuffix + "*" + ExportTempExtension;
                foreach (var abandoned in Directory.GetFiles(dir, pattern))
                {
                    Serilog.Log.Warning(
                        "[SqliteCipherHelper] Deleting {Abandoned}, a partly-written export left beside {Source} by a run that did not finish. A half-written export opens, reads and writes exactly like a healthy store while holding none of the data, so it is never used — it is rebuilt from the source instead",
                        abandoned, sourcePath);
                    DeleteFileAndSidecars(abandoned);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex,
                    "[SqliteCipherHelper] Could not look for abandoned exports beside {Source}; the export below writes its own file and does not use theirs",
                    sourcePath);
            }
        }

        /// <summary>
        /// Deletes a database file and every sibling SQLite may have written beside it. Silent by
        /// design — it is only ever used on files this class created seconds earlier, and a
        /// leftover there is reported by the caller with the context that makes it mean something.
        /// </summary>
        private static void DeleteFileAndSidecars(string path)
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            {
                try { if (File.Exists(candidate)) File.Delete(candidate); }
                catch (IOException) { /* held; the caller's report covers the leftover */ }
                catch (UnauthorizedAccessException) { /* same */ }
            }
        }

        /// <summary>
        /// The policy refusal. This run has not renamed, moved, copied or deleted anything when it
        /// is thrown, and that — not "the file is unchanged" — is what it says.
        ///
        /// <para>⚠ THE SIXTEENTH INSTANCE OF THIS TREE'S OVER-CLAIM CLASS, fixed 2026-08-08. The
        /// sentence used to read "NOTHING HAS BEEN CHANGED ON DISK: the existing file is still at
        /// its own path under its own name", and the first clause is measurably false while the
        /// second is true. Measured: a REFUSE store encrypted under a foreign key with a hot
        /// 41,232-byte -wal went from 4096 bytes / sha 4280B14029593C9B to 28,672 bytes / sha
        /// 44F6F58046625A43, and the -wal was gone, BEFORE this message printed. Nothing here did
        /// that — <see cref="IsKeyValid"/>'s probe opens the database, and closing that connection
        /// checkpoints the WAL into the main file, which probe (i) had already recorded 550 lines
        /// above in this same file. The contradicting measurement was in the tree and the sentence
        /// was simply not conditioned on it. So the claim is now scoped to the actions this code
        /// took, which is all it can know, and the rewrite the probe causes is stated rather than
        /// denied.</para>
        /// </summary>
        private static IOException ReinitRefused(string connectionString, string? path, StoreExport export)
        {
            var name = path is null ? "(no file)" : Path.GetFileName(path);
            var message =
                $"SQLTriage will not re-initialise the encrypted store '{name}' at '{path}'. Its contents could not "
                + $"be carried across ({export.Detail}), and this store is on the list that must not be handed back "
                + "empty because what it holds is entered by an operator (or is a licence record) and nothing "
                + "regenerates it. THIS RUN HAS NOT RENAMED, MOVED, COPIED OR DELETED IT: the existing file is "
                + "still at its own path under its own name, and no re-initialised store has been put in its "
                + "place. That is not the same as byte-for-byte untouched — testing the key OPENS the database, "
                + "and SQLite's own recovery can rewrite it on the way past (measured: a store with an "
                + "uncheckpointed -wal had the -wal folded into the main file and removed, and still read back "
                + "under the key that opens it). Either restore the cipher key that opens it "
                + "(config/.sqlite-cipher-key, and look for a .corrupt-* copy of it beside it), or move the file "
                + "aside by hand to accept starting this store empty.";

            Serilog.Log.Error("[SqliteCipherHelper] {Message}", message);
            return new IOException(message);
        }

        /// <summary>
        /// The refusal for an export that was built and verified and then could not be installed.
        /// Distinct from <see cref="ReinitRefused"/> because the disk is in a different state: the
        /// old file HAS been renamed aside by now, and the operator needs to be told where it is.
        /// </summary>
        private static IOException ExportInstallFailed(string connectionString, StoreExport export)
        {
            var message =
                $"SQLTriage exported the contents of the store at '{connectionString}' into a new encrypted "
                + $"database and then could not put it in place ({export.Detail}). The store was NOT re-initialised "
                + "empty, because that would have destroyed a readable copy whose contents this run had just proved "
                + "it could carry. The pre-re-init copy is on disk beside the store — see the line above naming it — "
                + "and renaming it back to the store's own name restores what was there — along with any "
                + "'-wal'/'-shm' sibling files sitting beside it, which must be moved back with it or their "
                + "unflushed pages are lost.";

            Serilog.Log.Error("[SqliteCipherHelper] {Message}", message);
            return new IOException(message);
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
        /// Reads back out of the freshly re-initialised store, and throws if what comes back is
        /// not what was just written.
        ///
        /// <para>Added 2026-08-06 with the aside ruling, and it exists for one reason: nothing may
        /// authorise deleting a copy of the operator's data without EVIDENCE that the replacement
        /// works. Necessary, not sufficient — this measures that the store READS, and
        /// <see cref="RemoveVerifiedPlaintextAside"/> separately measures that it is not a plain
        /// file, because those are two different claims and only the second one justifies the
        /// delete. "Open() returned" is not evidence of either — SQLCipher opens a file
        /// it cannot decrypt perfectly happily, which is the entire reason <see cref="IsKeyValid"/>
        /// has to probe rather than trust. "CommitEncryption did not throw" is closer but it is a
        /// WRITE; the 2026-05-21 note above records a state where writes land and the next read of
        /// the schema throws. So the proof is a read, of the table the write just created, out of
        /// the file the caller is about to be handed.</para>
        ///
        /// <para>Throwing here is the correct outcome, not a regression: the caller's catch
        /// disposes the connection and unwinds, and the copy is still on disk. A re-init that
        /// cannot prove itself must leave the operator everything they had.</para>
        ///
        /// <para>Internal, not private, so the read-back's own contract is tested rather than only
        /// inferred from the end-to-end path (InternalsVisibleTo SQLTriage.Tests). No end-to-end
        /// test can single this step out — every way of breaking a re-init from outside the process
        /// breaks it at Open or at CommitEncryption, earlier than here — so without a direct test
        /// this check is asserted by nothing.</para>
        ///
        /// <para>Returns what it MEASURED rather than void — see <see cref="StoreReadback"/>. The
        /// lines written after it have to be conditioned on the same measurement, so the numbers
        /// travel with the verdict instead of being re-derived by prose.</para>
        /// </summary>
        internal static StoreReadback VerifyReinitialisedStore(SqliteConnection conn, string connectionString)
        {
            using (var schema = conn.CreateCommand())
            {
                schema.CommandText =
                    "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '_sqlcipher_init';";
                var found = Convert.ToInt64(schema.ExecuteScalar() ?? 0L);
                if (found != 1)
                {
                    throw new IOException(
                        $"SQLTriage re-initialised the encrypted store at '{connectionString}', but reading its "
                        + "schema back did not find the table that had just been created in it. The new store is "
                        + "not trustworthy, so it was abandoned and nothing beside it was removed.");
                }
            }

            // COUNTED, not reasoned about. The line that tells the operator their store came back
            // empty is the most consequential sentence on this whole path — for check-baselines.db
            // the emptiness IS the event — and "a file created three statements ago cannot hold
            // anything" is an inference, not a measurement. This file's standing rule is that a
            // sentence beside a verdict is conditioned on the same measurement or is not printed,
            // so the emptiness is read out of the store that is about to be handed over.
            long otherTables;
            using (var others = conn.CreateCommand())
            {
                others.CommandText =
                    "SELECT count(*) FROM sqlite_master WHERE type = 'table' "
                    + "AND name <> '_sqlcipher_init' AND name NOT LIKE 'sqlite_%';";
                otherTables = Convert.ToInt64(others.ExecuteScalar() ?? 0L);
            }

            // And a read of the table's own page, not just the schema page — the 2026-05-21
            // half-encrypted state let one succeed while the other did not.
            using (var rows = conn.CreateCommand())
            {
                rows.CommandText = "SELECT count(*) FROM _sqlcipher_init;";
                rows.ExecuteScalar();
            }

            return new StoreReadback(MarkerTableRead: true, OtherTables: otherTables);
        }

        /// <summary>
        /// What reading the replacement store back actually measured: that the marker table this
        /// re-init created was found and read, and how many tables of its own the store holds
        /// besides it.
        ///
        /// <para>Data, not a capability token — and the difference matters, because a token is what
        /// used to be here. A <c>ReinitProof</c> record was passed from the read-back into the
        /// delete, and this file claimed the compiler therefore held the ruling's ordering: "putting
        /// the delete first stops compiling". It does not. The record was internal, so
        /// <c>new ReinitProof(connectionString)</c> compiled anywhere in this assembly; the
        /// adversarial pass on 2026-08-06 deleted the copy first, forged a proof, and the whole
        /// suite still passed. A false claim about a guarantee is worse than no guarantee, so the
        /// claim and the ceremony both went, and the ordering is now held by there being ONE
        /// function that does these steps in order (<see cref="CompleteReinitialisation"/>) with one
        /// call site per entry point.</para>
        ///
        /// <para>What survives is a check on a real measurement: <see cref="MarkerTableRead"/> is
        /// false in a <c>default</c> value, and the deletion refuses a reading that does not carry
        /// it. That is not a security boundary — anyone editing this file can write <c>true</c> —
        /// and it is not described as one.</para>
        /// </summary>
        internal readonly record struct StoreReadback(bool MarkerTableRead, long OtherTables);

        /// <summary>
        /// Everything that happens once the replacement store is open: prove it reads back, tell
        /// the operator what they now have, and only then — and only for the readable population —
        /// remove the copy.
        ///
        /// <para>THE ORDER IS THE RULING (Adrian, 2026-08-06, Option B). It is one function with
        /// one call site from each entry point precisely so the order is a thing that exists in one
        /// place. The earlier shape — three expressions at both call sites, held in order by a
        /// value passed between them — was defeated in a two-line edit with the suite still green.
        /// This shape is not proof against a determined edit either; nothing in a single assembly
        /// is. It is a smaller target, and it is not accompanied by a claim it cannot keep.</para>
        ///
        /// <para>The read-back THROWS on failure and that throw is deliberately not caught here:
        /// the caller's catch disposes the connection, <see cref="ReportAsideSurvivedFailure"/>
        /// records that the copy is untouched, and the operator keeps everything they had. Nothing
        /// below the throw runs — that is point 4 of the ruling, and it is why the throw is the
        /// first statement rather than a flag consulted later.</para>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests) so the sequence can be driven with a
        /// connection a test owns.</para>
        /// </summary>
        internal static void CompleteReinitialisation(
            SqliteConnection conn, string connectionString, ReinitAside? aside, StoreExport export)
        {
            var readback = VerifyReinitialisedStore(conn, connectionString);
            ReportReinitialisedStore(connectionString, readback, export);
            RemoveVerifiedPlaintextAside(connectionString, aside, readback, export);
        }

        /// <summary>
        /// Says what the operator actually has now.
        ///
        /// <para>It exists because the success path had no line for the one fact that matters most
        /// to whoever reads this log: the store came back EMPTY. Everything else written on this
        /// path is about files and keys. For check-baselines.db the emptiness is the whole event —
        /// accepted findings an operator ticked off one at a time are not in the new store, are not
        /// carried anywhere, and nothing was saying so. Worse, before this the only line about the
        /// deletion told them the copy "was no longer the only surviving version", which was false
        /// on disk and was the struck "no master data is lost" claim reappearing as the
        /// justification for an irreversible delete.</para>
        ///
        /// <para>Every branch prints a measured count, including the ones that are not expected to
        /// occur on this path. That is the point: the sentence is conditioned on the measurement
        /// rather than on the reasoning that a store created seconds ago must be empty.</para>
        ///
        /// <para>2026-08-08: there is now a THIRD sentence to be able to write, because a re-init
        /// can now carry the old contents across. The counts it prints come from the export's own
        /// verified reading (<see cref="StoreExport"/>), and the tables count comes from the
        /// read-back of the store being handed over — two measurements of two different things,
        /// both printed, neither inferred from the other.</para>
        /// </summary>
        private static void ReportReinitialisedStore(
            string connectionString, StoreReadback readback, StoreExport export)
        {
            if (export.Carried)
            {
                Serilog.Log.Warning(
                    "[SqliteCipherHelper] Re-initialised the store at {ConnStr} and CARRIED ITS CONTENTS ACROSS: {Objects} schema object(s) and {Rows} row(s) were exported out of the unencrypted predecessor and measured equal to it before the swap. The store now reads back and holds {OtherTables} table(s) of its own besides the marker this re-init created. What was NOT carried is anything the export cannot see: this is a copy of the database's contents, not of the file",
                    connectionString, export.Objects, export.Rows, readback.OtherTables);
            }
            else if (readback.OtherTables == 0)
            {
                Serilog.Log.Warning(
                    "[SqliteCipherHelper] Re-initialised the store at {ConnStr}. It reads back, and it is EMPTY: besides the marker table this re-init created it holds no tables at all. Nothing was carried across, because {Why:l}. This store is on the list that accepts an empty re-init because its contents refill from elsewhere — see the fallback registry in SqliteCipherHelper, which names what each store holds",
                    connectionString, export.Detail ?? "no export was recorded");
            }
            else
            {
                Serilog.Log.Warning(
                    "[SqliteCipherHelper] Re-initialised the store at {ConnStr}. It reads back and holds {OtherTables} table(s) of its own besides the marker this re-init created, but nothing was carried across by this re-init, because {Why:l} — so anything in them was made after it rather than recovered by it",
                    connectionString, readback.OtherTables, export.Detail ?? "no export was recorded");
            }
        }

        /// <summary>
        /// Deletes the pre-re-init copy, and only once every one of the ruling's conditions has been
        /// MEASURED: the copy's own first 16 bytes were the plain SQLite header, the replacement
        /// store read back, and the replacement store's own first 16 bytes are not the plain header.
        ///
        /// <para>That last condition is the one the first draft was missing, and the gap was a
        /// mismatch between the evidence and the justification. The delete was authorised by
        /// READABILITY (the read-back) but justified by ENCRYPTION — the copy is destroyed because
        /// it is the version someone could read off a stolen backup, which only holds if what
        /// replaced it cannot be. Nothing in <see cref="VerifyReinitialisedStore"/> measures that:
        /// both of its reads succeed against a plain file just as well. And this is not theoretical
        /// in this file — the 2026-05-21 note on <see cref="IsKeyValid"/> records an observed state
        /// where a store materialised an UNENCRYPTED header after Open, and a provider mis-binding
        /// (e_sqlite3 loaded where e_sqlcipher was meant) produces the same thing. So the
        /// replacement is sniffed with the same discriminator used on the copy, and the answer must
        /// be <see cref="FileHeadReading.OtherBytes"/>: MEASURED as not plain. "Did not come back
        /// plain" is not enough — a sniff that failed establishes nothing and authorises nothing.</para>
        ///
        /// <para>Does not throw on a failed delete. By the time this runs the encrypted store is
        /// open and usable, and failing the caller's open over a leftover file would trade a working
        /// store for a tidy directory — at one of 25 OpenEncrypted consumers, which under the
        /// Windows SCM is a startup restart loop. A delete that fails is reported at Error naming
        /// the path, because the honest claim in that case is "the store is open, and this readable
        /// copy of its predecessor is still sitting next to it".</para>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests), and it has to be: no end-to-end test
        /// can make File.Delete fail here, because the copy does not exist until the Move and
        /// nothing can take a handle on it between the Move and this line. Left private, the
        /// no-throw contract and the encryption gate were asserted by nothing — a mutation making
        /// the delete rethrow passed all 17 tests on 2026-08-06.</para>
        /// </summary>
        internal static void RemoveVerifiedPlaintextAside(
            string connectionString, ReinitAside? aside, StoreReadback readback, StoreExport export)
        {
            if (aside is not { } left) return;

            if (!readback.MarkerTableRead)
            {
                // A default reading: nothing was read back, so nothing may be deleted.
                Serilog.Log.Error(
                    "[SqliteCipherHelper] Kept {Aside}: this was reached without a completed read-back of the replacement store, so nothing has established that the replacement works. That is a coding error in this file, not an operator problem — the copy is where it was renamed",
                    left.Path);
                return;
            }

            if (left.Content != AsideContent.Plaintext)
            {
                // The unreadable population: a SQLCipher file under a key nobody has, or corrupt.
                // Deleting one protects no one and destroys the only copy. Nothing happened to it
                // here, and the line naming it was written at the rename, so there is nothing to
                // add — a line here would only be able to say that nothing was done.
                return;
            }

            var storePath = TryGetDataSourcePath(connectionString);
            var storeHead = storePath is null ? FileHeadReading.Unmeasured : ReadFileHead(storePath);
            if (storeHead != FileHeadReading.OtherBytes)
            {
                Serilog.Log.Error(
                    "[SqliteCipherHelper] Kept the UNENCRYPTED pre-re-init copy {Aside}. The replacement store at {ConnStr} reads back, but its own first 16 bytes {StoreHead}, so this run has NOT established that the replacement is encrypted — and that is the entire justification for destroying a readable copy. The copy IS readable by anyone who can read this folder or a backup of it, and on this evidence the replacement may be too. Look at the store before deleting the copy by hand",
                    left.Path, connectionString, DescribeHead(storeHead));
                return;
            }

            try
            {
                File.Delete(left.Path);
                // The siblings are the SAME PLAINTEXT. Probe (h) on 2026-08-08 measured a WAL-mode
                // store whose entire schema and all 5000 of its rows lived in the -wal and NOT in
                // the main file, so deleting the main copy and leaving its -wal beside it would
                // leave behind, in the worst case, everything the delete was for.
                var strandedSidecars = new System.Collections.Generic.List<string>();
                foreach (var sidecar in left.SidecarPaths)
                {
                    try { if (File.Exists(sidecar)) File.Delete(sidecar); }
                    catch (Exception sidecarEx)
                    {
                        strandedSidecars.Add(sidecar);
                        Serilog.Log.Error(sidecarEx,
                            "[SqliteCipherHelper] Deleted the pre-re-init copy but could NOT delete its unencrypted sibling {Sidecar}, which can hold pages the main file never had. Delete it by hand",
                            sidecar);
                    }
                }

                if (export.Carried)
                {
                    Serilog.Log.Warning(
                        "[SqliteCipherHelper] Deleted the pre-re-init copy {Aside}{Stranded:l}. Its first 16 bytes were the plain SQLite header, so it was an UNENCRYPTED database that anyone with this folder or a backup of it could read without the key — the one thing DPAPI-LocalMachine encryption buys, since the key sits in the same install tree. The replacement store at {ConnStr} read back and its own first 16 bytes are not the plain header, both measured before this delete ran. Its CONTENTS are not gone: {Objects} schema object(s) and {Rows} row(s) were exported into the replacement and measured equal to this copy before it was removed",
                        left.Path, StrandedClause(left.SidecarPaths.Count, strandedSidecars), connectionString, export.Objects, export.Rows);
                }
                else
                {
                    Serilog.Log.Warning(
                        "[SqliteCipherHelper] Deleted the pre-re-init copy {Aside}{Stranded:l}. Its first 16 bytes were the plain SQLite header, so it was an UNENCRYPTED database that anyone with this folder or a backup of it could read without the key — the one thing DPAPI-LocalMachine encryption buys, since the key sits in the same install tree. The replacement store at {ConnStr} read back and its own first 16 bytes are not the plain header, both measured before this delete ran. ⚠ The copy's CONTENTS are NOT preserved anywhere: nothing was carried across, because {Why:l}. What was in it is gone with it",
                        left.Path, StrandedClause(left.SidecarPaths.Count, strandedSidecars), connectionString, export.Detail ?? "no export was recorded");
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex,
                    "[SqliteCipherHelper] The replacement store for {ConnStr} is open, reads back, and is not a plain file, but the UNENCRYPTED pre-re-init copy at {Aside} could not be deleted and is STILL THERE. Anyone who can read that folder — or a backup of it — can read it without the key. Delete it by hand",
                    connectionString, left.Path);
            }
        }

        /// <summary>
        /// The clause the delete line carries about the copy's unencrypted siblings — so the
        /// sentence "the copy was deleted" is never printed alone over a folder that still holds
        /// part of it, and so a run that had no siblings does not claim to have removed any.
        /// </summary>
        private static string StrandedClause(
            int siblings, System.Collections.Generic.IReadOnlyList<string> stranded)
        {
            if (siblings == 0) return string.Empty;
            if (stranded.Count == 0) return $" and all {siblings} unencrypted sibling file(s) beside it";
            return $" but {stranded.Count} of its {siblings} unencrypted sibling file(s) could NOT be removed and are"
                + " STILL ON DISK (" + string.Join(", ", stranded) + ")";
        }

        /// <summary>
        /// The clause naming what a sniff of the replacement store actually established, so the
        /// refusal above reports a measurement (or the absence of one) rather than a verdict.
        /// </summary>
        private static string DescribeHead(FileHeadReading reading) => reading switch
        {
            FileHeadReading.PlainSqliteHeader => "ARE the plain SQLite header, so the replacement is not encrypted either",
            FileHeadReading.OtherBytes => "were read and are not the plain SQLite header",
            FileHeadReading.TooShort => "are not all there (the file is shorter than 16 bytes), so nothing was measured",
            _ => "could not be read at all, so nothing was measured",
        };

        /// <summary>
        /// The store's own file path, taken off the connection string. Null when there is not one —
        /// an in-memory source, an empty source, or a string that will not parse — and null is the
        /// refusing answer everywhere it is used.
        /// </summary>
        private static string? TryGetDataSourcePath(string connectionString)
        {
            try
            {
                var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
                return string.IsNullOrEmpty(path) || path == ":memory:" ? null : path;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex,
                    "[SqliteCipherHelper] Could not read a file path out of the connection string {ConnStr}",
                    connectionString);
                return null;
            }
        }

        /// <summary>
        /// Says what became of the copy when the re-init did not complete: nothing did. Written on
        /// the unwind path so the log never simply stops after "renamed aside" and leaves a reader
        /// to guess whether the file survived.
        /// </summary>
        private static void ReportAsideSurvivedFailure(ReinitAside? aside)
        {
            if (aside is not { } left) return;

            Serilog.Log.Error(
                "[SqliteCipherHelper] Re-initialisation did not complete, so nothing was done to {Aside} — it is on disk exactly as it was renamed",
                left.Path);
        }

        /// <summary>
        /// The two populations of pre-re-init copy, which deserve opposite treatment and are told
        /// apart by <see cref="ClassifyDatabaseFile"/>.
        /// </summary>
        internal enum AsideContent
        {
            /// <summary>
            /// The file begins with the plain SQLite header, so it is an UNENCRYPTED database that
            /// anyone who can read the folder can read. This is the population the 2026-08-06
            /// ruling deletes — after, and only after, the replacement store has been read back AND
            /// measured as not itself a plain file. Being in this population is a necessary
            /// condition for the delete, never a sufficient one.
            /// </summary>
            Plaintext,

            /// <summary>
            /// Anything else: a SQLCipher file under a key that no longer opens it (the DPAPI-fault
            /// path in <see cref="LoadOrGenerateHexKey"/> regenerates the key and orphans every
            /// store), a corrupt file, a file too short to classify, or one the sniff could not
            /// read at all. Nobody can read these, including us, so deleting one destroys the only
            /// remaining copy of the operator's data and protects nobody. Kept indefinitely.
            /// ⚠ This is also the FAIL-SAFE value: every uncertain classification lands here.
            /// </summary>
            Unreadable,
        }

        /// <summary>
        /// What the re-init left beside the store, and whether it is readable by a finder.
        /// Internal (InternalsVisibleTo SQLTriage.Tests) so the deletion's own contract — the
        /// no-throw guarantee, the encryption gate, the refusal of an unverified reading — can be
        /// driven directly, which no end-to-end test can reach.
        ///
        /// <para><paramref name="Sidecars"/> are the -wal/-shm/-journal files that ended up beside
        /// the copy under its name — renamed there, or COPIED there when the rename could not run
        /// (see <see cref="MoveSidecarsBeside"/>). They are part of the copy, not debris: probe (h)
        /// on 2026-08-08 measured a WAL-mode store whose whole schema and all 5000 rows were in the
        /// -wal and whose main file alone read back <c>no such table</c>. Until that date this class
        /// DELETED them and kept only the main file, so the aside it promised as a rollback source
        /// could be an empty database. ⚠ This list is what is BESIDE the copy, so the plaintext
        /// delete removes exactly these — a sibling that reached the copy by being copied still has
        /// an original at the store's own path, and that original is not this class's to keep.</para>
        /// </summary>
        internal readonly record struct ReinitAside(string Path, AsideContent Content, string[]? Sidecars)
        {
            /// <summary>
            /// The sidecars, never null — a <c>default</c> value has none rather than throwing at
            /// whichever line reads it first.
            /// </summary>
            internal System.Collections.Generic.IReadOnlyList<string> SidecarPaths =>
                Sidecars ?? Array.Empty<string>();
        }

        /// <summary>The 16-byte magic every plain SQLite database file starts with.</summary>
        private static readonly byte[] PlainSqliteHeader =
            System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");

        /// <summary>
        /// What the first 16 bytes of a file turned out to be — with the two ways of NOT finding
        /// out kept apart from each other and, above all, apart from a measurement.
        ///
        /// <para>Four values rather than a bool because the log lines and the deletion gate both
        /// need to tell "measured, and these bytes are not the plain header" from "nothing was
        /// measured". Collapsing them is how the first draft came to print "It is not a plain
        /// SQLite file" over a sniff that had thrown and established nothing: a fail-safe DEFAULT
        /// presented to an operator as a measurement. That is the defect class this file keeps
        /// having to remove, and it comes back every time a reading and a decision are the same
        /// value.</para>
        ///
        /// <para>They also fail in OPPOSITE directions depending on which file is being sniffed,
        /// which a two-valued answer cannot express. For the copy, "not measured" must mean PRESERVE.
        /// For the replacement store, "not measured" must mean DO NOT DELETE the copy. Both are the
        /// cautious answer; they are not the same answer.</para>
        /// </summary>
        internal enum FileHeadReading
        {
            /// <summary>Measured: the 16 bytes are the plain SQLite header, so it is a readable database.</summary>
            PlainSqliteHeader,

            /// <summary>Measured: 16 bytes were read and they are not the plain header — a SQLCipher salt, or corruption.</summary>
            OtherBytes,

            /// <summary>Measured only that there are fewer than 16 bytes. Nothing is known about the contents.</summary>
            TooShort,

            /// <summary>NOT measured: the read itself failed. Nothing whatever is known about the contents.</summary>
            Unmeasured,
        }

        /// <summary>
        /// Reads the first 16 bytes of <paramref name="path"/> and reports what they were, or which
        /// way the reading failed. Says nothing about what will be done with the answer — that is
        /// the caller's sentence to write, and it differs by which file is being sniffed.
        ///
        /// <para>Its two warnings were previously worded "treating it as unreadable and keeping it",
        /// which became false the moment this sniff was also pointed at the REPLACEMENT store,
        /// where nothing is kept or discarded on its account. A line stating a consequence the
        /// function cannot know is the same defect in miniature.</para>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests): it is the single measurement both
        /// the deletion and the rename log hang off, and its boundary cases — the short file, the
        /// unreadable file — are exactly the ones no end-to-end path exercises.</para>
        /// </summary>
        internal static FileHeadReading ReadFileHead(string path)
        {
            try
            {
                var head = new byte[PlainSqliteHeader.Length];
                // FileShare.ReadWrite | Delete so sniffing never becomes the reason a file cannot
                // be moved: this open is a read on the way past, not a claim on the file. It is
                // also what lets the live replacement store be sniffed while SQLite holds it open.
                using (var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var read = 0;
                    while (read < head.Length)
                    {
                        var got = stream.Read(head, read, head.Length - read);
                        if (got == 0)
                        {
                            Serilog.Log.Warning(
                                "[SqliteCipherHelper] {Path} is only {Len} bytes, fewer than the 16 a database header needs, so its contents were not classified",
                                path, read);
                            return FileHeadReading.TooShort;
                        }
                        read += got;
                    }
                }

                return head.AsSpan().SequenceEqual(PlainSqliteHeader)
                    ? FileHeadReading.PlainSqliteHeader
                    : FileHeadReading.OtherBytes;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex,
                    "[SqliteCipherHelper] Could not read the first bytes of {Path}, so nothing is known about its contents",
                    path);
                return FileHeadReading.Unmeasured;
            }
        }

        /// <summary>
        /// Which population a pre-re-init copy belongs to, from <see cref="ReadFileHead"/>.
        ///
        /// <para>The discrimination is the whole of it: a plain SQLite file opens with the ASCII
        /// header <c>SQLite format 3\0</c>; a SQLCipher file opens with its 16-byte random salt.
        /// Equal means PLAINTEXT — a readable database. Anything else means keep it.</para>
        ///
        /// <para>⚠ FAIL-SAFE, and the direction matters. Every reading that is not a measured plain
        /// header — the file is shorter than one, the read threw, the file vanished between the
        /// check and here — comes back <see cref="AsideContent.Unreadable"/>, because that is the
        /// answer that PRESERVES. The cost of a wrong "unreadable" is a stale file an operator can
        /// delete; the cost of a wrong "plaintext" is destroying the only copy of
        /// check-baselines.db. Never guess towards the irreversible one.</para>
        ///
        /// <para>⚠ And note what this collapse costs, which is why the raw reading is kept
        /// alongside it: an <c>Unreadable</c> here does NOT mean the file was measured and found
        /// unreadable. Anything printed about WHY has to come from the reading, not from this.</para>
        ///
        /// <para>Internal, not private, so the tests can exercise the discriminator directly on
        /// both populations (InternalsVisibleTo SQLTriage.Tests). It is the single decision the
        /// deletion hangs off.</para>
        /// </summary>
        internal static AsideContent ClassifyDatabaseFile(string path) =>
            ReadFileHead(path) == FileHeadReading.PlainSqliteHeader
                ? AsideContent.Plaintext
                : AsideContent.Unreadable;

        /// <summary>
        /// Attempts to clear the .db file (renamed aside first, so it is never destroyed unread)
        /// and its WAL/SHM siblings, and REPORTS whether the main path is really gone — and what
        /// it left beside it.
        ///
        /// <para>⚠ "Clear" means the PATH is clear. The old file is renamed to
        /// <c>&lt;store&gt;.db.pre-reinit-&lt;utc timestamp&gt;</c> and that copy's fate is
        /// decided by the CALLER, not here — see the ruling at the rename.</para>
        ///
        /// <para>It used to be a void called "best-effort", and the caller reopened at the same
        /// path regardless. When the move and the delete both failed the old, wrong-keyed file was
        /// still sitting there, so the reopen's CommitEncryption threw "malformed database schema"
        /// out of whichever of the 25 OpenEncrypted consumers happened to be constructing — an
        /// error about SQL schema, for a problem that was a locked file, at a call site that had
        /// nothing to do with either. Under the Windows SCM, a store constructed during startup
        /// turns that into a restart loop with no diagnosable cause.</para>
        ///
        /// <para>Two changes, and the first is the one that matters in practice: the removal is
        /// RETRIED. A handle SQLite has not finished releasing is the common reason this fails, and
        /// it is transient — pooled connections are only queued for finalisation when
        /// ClearAllPools returns. Draining finalisers and trying again converts most of these into
        /// a clean removal, which is the loop prevented rather than reported.</para>
        ///
        /// <para>The second: when it still cannot be cleared, the caller is TOLD, and refuses to
        /// open a connection that is certain to fail. ⚠ Stated plainly rather than overclaimed —
        /// that turns an undiagnosable restart loop into a diagnosable one. Whether a given store
        /// is optional enough to start without is a decision at the 25 consumers, not here.</para>
        /// </summary>
        /// <param name="blockingPath">The path still present, when this returns false.</param>
        /// <param name="aside">
        /// The copy this call renamed aside and its classification, or null if it renamed nothing
        /// (the file was already gone, or the move never succeeded). The caller owns what happens
        /// to it next — see <see cref="RemoveVerifiedPlaintextAside"/>.
        /// </param>
        private static bool TryClearDbFile(
            string connectionString, out string? blockingPath, out ReinitAside? aside)
        {
            blockingPath = null;
            aside = null;
            string? path = null;
            try
            {
                // One definition of "the store's own file", shared with the deletion's sniff of the
                // replacement — two parses of the same connection string is two places to disagree.
                path = TryGetDataSourcePath(connectionString);
                if (path is null) return true;

                // What became of the siblings, carried out of the loop because the sweep below has
                // to know which of them are the COPY rather than debris. SidecarOutcome.None and
                // not default(SidecarOutcome): a default struct has NULL arrays, and this value is
                // read whether or not the rename ever ran. None means "nothing was renamed, so
                // nothing at the original path is being kept", which is the right answer for a run
                // that never promised an aside.
                var siblings = SidecarOutcome.None;

                // The main file first: it is the one the verify is about. Its siblings follow it to
                // the aside — see MoveSidecarsBeside, and see why the old "delete them" was wrong.
                for (var attempt = 1; attempt <= ClearAttempts; attempt++)
                {
                    SqliteConnection.ClearAllPools();
                    if (attempt > 1)
                    {
                        // ClearAllPools QUEUES pooled connections for finalisation; it does not
                        // wait for them. Until the finaliser runs, the OS handle is still open and
                        // every File.Move/Delete on Windows fails with a sharing violation.
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        Thread.Sleep(ClearRetryDelayMs);
                    }

                    if (!File.Exists(path)) break;

                    // H2 defense-in-depth (2026-07-07): preserve the main db rather than destroy
                    // it. If the re-init was a misclassification, the operator can recover the
                    // renamed file.
                    //
                    // ── THE ARCHAEOLOGY, kept because it is why this looks the way it does ──
                    // The rename was written on 2026-07-07 and DID NOT RUN ONCE until 2026-08-06.
                    // OpenEncrypted still had its own connection open on this very file, Windows
                    // refuses to move a file with a live handle, and the delete fallback beside it
                    // failed for the same reason — both swallowed, and the code reopened anyway. So
                    // for a month the H2 "preserve it aside" defence existed only on paper, and the
                    // question of what the aside costs had never had to be asked. The Wave-3 dispose
                    // fix (see OpenEncrypted) made the rename start working, which is what put the
                    // aside's lifecycle on the table at all.
                    //
                    // ── ADRIAN'S RULING, 2026-08-06 (Option B) — implemented below ──
                    // There are TWO populations of aside here and they deserve opposite treatment:
                    //
                    //   PLAINTEXT  — from the plain→encrypted migration, the common path. This is a
                    //     byte-for-byte copy of an UNENCRYPTED database. The key is DPAPI-wrapped at
                    //     LocalMachine scope in config/ INSIDE THE SAME INSTALL TREE as the stores,
                    //     so on-box the encryption buys nothing against anyone who is already on the
                    //     box; what it really protects is the folder-leaves-the-machine case — a
                    //     backup, a support bundle, a copied install — because DPAPI LocalMachine
                    //     will not unwrap elsewhere. A plaintext copy sitting beside the store
                    //     defeats exactly that protection, and only that one. It is deleted.
                    //
                    //   UNREADABLE — a SQLCipher file under a key that no longer exists (the DPAPI
                    //     regenerate path), or corrupt. Nobody can read it, including Adrian, so it
                    //     leaks nothing and it is the only surviving copy of whatever was in it.
                    //     Behaviour unchanged: kept indefinitely, nothing here deletes it.
                    //
                    // The classification is made HERE, before the move, off the file's first 16
                    // bytes; the deletion happens at the CALLER, after the replacement store has
                    // been proven to open and read back. That ordering is the ruling's substance —
                    // a re-init that fails leaves the operator everything they had.
                    var asidePath = path + ".pre-reinit-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                    // The raw reading is kept, not just the population it maps to: the log line
                    // below has to be able to say WHICH of the three non-plain grounds it is on,
                    // and AsideContent.Unreadable cannot tell them apart.
                    var reading = ReadFileHead(path);
                    var content = reading == FileHeadReading.PlainSqliteHeader
                        ? AsideContent.Plaintext
                        : AsideContent.Unreadable;
                    try
                    {
                        File.Move(path, asidePath);
                        // The siblings follow immediately, before anything else can look at either
                        // path — they are part of the copy, not debris. See MoveSidecarsBeside.
                        siblings = MoveSidecarsBeside(path, asidePath);
                        aside = new ReinitAside(asidePath, content, siblings.Beside);
                        LogRenamedAside(asidePath, reading, siblings);
                        break;
                    }
                    catch (IOException) { /* still held — next attempt */ }
                    catch (UnauthorizedAccessException) { /* ACL or read-only — next attempt */ }
                }

                RemoveStrandedSidecars(path, siblings.Lost);

                // THE VERIFY. Existence is the question, so existence is what is asked — not
                // whether an exception was thrown, which is how "best-effort" came to mean
                // "unchecked".
                if (File.Exists(path))
                {
                    blockingPath = path;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[SqliteCipherHelper] Failed to re-initialise plain SQLite file during migration");
                // An unexpected failure while clearing is not evidence the file went away.
                blockingPath = path;
                return path is null || !File.Exists(path);
            }
        }

        /// <summary>The SQLite siblings a database file can have beside it, all of which can hold data.</summary>
        private static readonly string[] SidecarSuffixes = { "-wal", "-shm", "-journal" };

        /// <summary>
        /// Moves the store's -wal/-shm/-journal siblings so they sit beside the aside under its
        /// name, and reports which ones could not be moved. Replaced an unconditional DELETE
        /// (Adrian's ruling, 2026-08-08, point 4).
        ///
        /// <para>WHAT IS AT STAKE, measured. Probe (h), 2026-08-08: a WAL-mode store with an
        /// uncheckpointed WAL had its entire schema and all 5000 of its rows in the -wal. A copy of
        /// the main file alone read back <c>no such table: accepted</c>; a copy of main + -wal read
        /// back 5000. Deleting the sibling and keeping the main file can therefore preserve an
        /// EMPTY DATABASE and call it a rollback source.</para>
        ///
        /// <para>⚠ AND WHY THAT USUALLY DID NOT BITE, which the honest version of this note has to
        /// say. Probe (i), 2026-08-08, ran the key probe against a store in exactly that state:
        /// closing the probe connection CHECKPOINTED the WAL into the main file and deleted the
        /// -wal — for a plain store AND for one encrypted under a key this install does not have,
        /// and after the probe had already failed with SQLITE_NOTADB. The main file went from 4096
        /// to 20480 bytes and read back all 500 rows under its rightful key. So on the ordinary
        /// single-process path the old delete removed a file that was already gone, and the aside
        /// was complete by checkpoint rather than by preservation.</para>
        ///
        /// <para>It bites where the checkpoint does NOT happen — another connection or another
        /// process holding the database, which is the contended case this whole re-init path exists
        /// to handle. The checkpoint is a side effect nothing here controls or can require; this
        /// move is what makes the copy complete when it does not occur. Both, not either.</para>
        ///
        /// <para>SQLite derives a journal's name from its database's, so <c>&lt;aside&gt;-wal</c>
        /// beside <c>&lt;aside&gt;</c> is exactly where an operator renaming the copy back needs it
        /// to be.</para>
        ///
        /// <para>-journal is here as well as -wal/-shm, one past the ruling's letter: probe (d)
        /// measured an interrupted write leaving a hot 9,728-byte -journal, and a hot journal is
        /// rollback data for the file it sits beside. Deleting one is the same mistake in a
        /// different mode.</para>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests), and it has to be: the state it
        /// exists for is the one where the checkpoint did not run, and probe (i) shows a test
        /// cannot reach that from outside — the helper's own probe checkpoints the file on the way
        /// past. Left private, the case this function was written for is asserted by nothing.</para>
        ///
        /// <para>⚠ THE COPY FALLBACK (2026-08-08) IS NOT BELT-AND-BRACES, it is the only thing on
        /// this path that preserves anything when the rename fails. Probe (j) measured what
        /// happens to a sibling simply LEFT at the store's own path: the re-init creates a new
        /// database there moments later and SQLite discards the stale -wal on the way past — it is
        /// not adopted, it does not break the new store, and it is not there afterwards. So
        /// "we did not delete it" preserves nothing on its own. Probe (k) measured the asymmetry
        /// that makes the fallback work: on a sibling another handle holds with FileShare.Read,
        /// File.Move throws and File.Copy succeeds, because a rename needs FILE_SHARE_DELETE from
        /// every open handle and a read does not — and the contended sibling is precisely the case
        /// this path exists for. A copied sibling is counted as beside the copy; the original is
        /// then ordinary debris and the sweep may remove it.</para>
        ///
        /// <para>A copy taken from a file another process may still be writing is not guaranteed to
        /// be a consistent snapshot of it, and the log line says so rather than calling the copy
        /// complete. It is still the difference between some of those pages and none of them.</para>
        /// </summary>
        internal static SidecarOutcome MoveSidecarsBeside(string path, string asidePath)
        {
            var beside = new System.Collections.Generic.List<string>();
            var copied = new System.Collections.Generic.List<string>();
            var failed = new System.Collections.Generic.List<string>();

            foreach (var suffix in SidecarSuffixes)
            {
                var from = path + suffix;
                if (!File.Exists(from)) continue;
                var to = asidePath + suffix;
                try
                {
                    File.Move(from, to);
                    beside.Add(to);
                    continue;
                }
                catch (Exception moveEx)
                {
                    Serilog.Log.Warning(moveEx,
                        "[SqliteCipherHelper] Could not RENAME {Sidecar} beside the pre-re-init copy at {Aside}; trying to copy it instead. A -wal or -journal can hold rows the main file does not",
                        from, asidePath);
                }

                try
                {
                    File.Copy(from, to, overwrite: false);
                    copied.Add(to);
                    beside.Add(to);
                }
                catch (Exception copyEx)
                {
                    failed.Add(from);
                    Serilog.Log.Error(copyEx,
                        "[SqliteCipherHelper] Could neither rename nor copy {Sidecar} beside the pre-re-init copy at {Aside}. A -wal or -journal can hold rows the main file does not, so the copy is INCOMPLETE by exactly whatever is in this file. Nothing here deletes it — but opening a database at that path again can (SQLite discards a -wal that does not belong to the file beside it), so move it beside the copy by hand now",
                        from, asidePath);
                }
            }

            return new SidecarOutcome(beside.ToArray(), copied.ToArray(), failed.ToArray());
        }

        /// <summary>
        /// What became of the store's -wal/-shm/-journal siblings at the rename.
        ///
        /// <para><paramref name="Beside"/> are the ones now sitting beside the copy under its name,
        /// renamed or copied; <paramref name="Copied"/> is the subset that had to be copied, which
        /// the log has to say because a copy off a live writer is not a guaranteed snapshot;
        /// <paramref name="Lost"/> are the ones that could be neither, which are the only siblings
        /// the sweep may not delete.</para>
        ///
        /// <para>⚠ <c>default(SidecarOutcome)</c> holds three NULL arrays, as every struct default
        /// does. Use <see cref="None"/> for "the rename never ran"; every read site here also
        /// tolerates a null, because a NullReferenceException on the failure path of a data-loss
        /// guard is the guard not running.</para>
        /// </summary>
        internal readonly record struct SidecarOutcome(string[] Beside, string[] Copied, string[] Lost)
        {
            /// <summary>No siblings, in any of the three states — the value for a run that renamed nothing.</summary>
            internal static SidecarOutcome None { get; } =
                new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }

        /// <summary>
        /// Deletes the siblings still sitting at the STORE's own path after the rename, so SQLite
        /// does not find a stale -wal beside a brand new database — except any in
        /// <paramref name="preserved"/>, which is the whole reason this is a named function rather
        /// than four lines inside the loop above.
        ///
        /// <para>⚠⚠ A SIBLING IN <paramref name="preserved"/> IS NOT DEBRIS, IT IS THE COPY. It is
        /// what <see cref="MoveSidecarsBeside"/> could neither rename nor copy beside the aside, so
        /// it holds the only remaining version of whatever pages are in it — and by the time this
        /// runs the rename line has ALREADY told the operator that file is "still at the store's own
        /// path ... so the copy is missing whatever was in them". Until 2026-08-08 the sweep deleted
        /// it one statement later. Measured that day: a store whose -wal held the only copy of its
        /// pages, the sidecar move made to fail and the delete left to succeed, produced the log
        /// line naming the file and then "ORIGINAL -wal STILL ON DISK = False". Move-fails-but-
        /// delete-succeeds is not exotic — a destination-name collision, a path too long, or a
        /// directory ACL that permits delete and denies create all produce it.</para>
        ///
        /// <para>THREE reasons a sibling can still be at the original path, and the old comment
        /// here listed two. (1) The main file was never moved: no aside was promised, nothing is
        /// preserved and nothing claims to be. (2) A sibling appeared late, written by SQLite after
        /// the move. (3) A sibling failed to move while the main file succeeded — and only the
        /// third has an aside standing behind it. The first two are swept; the third is left.</para>
        ///
        /// <para>⚠ AND LEAVING IT IS NOT SAVING IT, which the log line has to say rather than imply.
        /// Probe (j), 2026-08-08: a 37,112-byte hot -wal left at a store path had the re-init's own
        /// next steps run against it, and the new encrypted store opened holding only its own marker
        /// while SQLite DISCARDED the stale -wal — gone by the time the connection closed, for a
        /// -wal written plain and for one written under a foreign key. So this refusal buys the
        /// operator the seconds before the store is opened again, not a rollback source. What
        /// actually preserves those pages is the copy fallback in <see cref="MoveSidecarsBeside"/>;
        /// this is the line of last resort behind it.</para>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests): the state it exists for cannot be
        /// reached end to end, because it needs a File.Move that fails where a File.Delete would
        /// succeed, against a destination name this class generates from the clock at the instant
        /// of the rename.</para>
        /// </summary>
        internal static void RemoveStrandedSidecars(string path, string[]? preserved)
        {
            var keep = preserved ?? Array.Empty<string>();

            foreach (var suffix in SidecarSuffixes)
            {
                var candidate = path + suffix;
                if (!File.Exists(candidate)) continue;

                if (Array.IndexOf(keep, candidate) >= 0)
                {
                    Serilog.Log.Error(
                        "[SqliteCipherHelper] Left {Sidecar} where it is. It could be neither renamed nor copied beside the pre-re-init copy, so it holds the only remaining version of whatever pages are in it and this run will not delete it. ⚠ That is not the same as saving it: this path goes on to create a new database at {Store}, and SQLite discards a -wal that does not belong to the file beside it. Move this file beside the copy by hand, or out of the folder, now",
                        candidate, path);
                    continue;
                }

                try { File.Delete(candidate); }
                catch (Exception ex)
                {
                    // ⚠ REPORTED SINCE 2026-08-08, where it used to be swallowed on the grounds
                    // that "a fresh db does not need it gone". True of the new store, and not the
                    // only thing at stake: the copy fallback in MoveSidecarsBeside deliberately
                    // leaves an ORIGINAL here whenever a sibling had to be copied rather than
                    // renamed, and a sibling of an unencrypted predecessor is itself unencrypted.
                    // Failing to remove one is not tidiness, so it is not silent.
                    Serilog.Log.Warning(ex,
                        "[SqliteCipherHelper] Could not remove {Sidecar}, a leftover sibling of the store's predecessor at {Store}. If that predecessor was an unencrypted database then this file is unencrypted too — remove it by hand",
                        candidate, path);
                }
            }
        }

        /// <summary>
        /// The one line written at the rename, and it states ONLY what the Move and the sniff have
        /// established at that instant: the file is now at the aside path, this is what its first
        /// 16 bytes were (or which way reading them failed), and this is what became of its
        /// siblings.
        ///
        /// <para>⚠ THE HISTORY OF THIS LINE IS THE REASON IT IS FOUR BRANCHES. It began as one
        /// sentence — "Preserved unreadable SQLite file as X (not deleted)" — which called a
        /// readable database unreadable on the common path and promised a preservation nothing was
        /// yet in a position to promise. That was split in two, and the non-plain branch then
        /// asserted "It is not a plain SQLite file" for all THREE grounds that reach it, including
        /// the one where the sniff threw and nothing whatever had been measured: the fail-safe
        /// default reprinted as a finding. Each grounds gets its own sentence because each ground is
        /// a different amount of knowledge.</para>
        ///
        /// <para>What becomes of the copy is not stated here, because at this instant it is not
        /// decided — the deletion runs later and has conditions of its own. The most these lines say
        /// about the future is what THIS code path does, which is a fact about the code and is held
        /// by the tests, not a prediction about this particular file.</para>
        /// </summary>
        private static void LogRenamedAside(
            string asidePath, FileHeadReading reading, SidecarOutcome sidecars)
        {
            var siblings = DescribeSidecars(sidecars);

            switch (reading)
            {
                case FileHeadReading.PlainSqliteHeader:
                    Serilog.Log.Warning(
                        "[SqliteCipherHelper] Renamed the existing SQLite file to {Aside}. Its first 16 bytes are the plain SQLite header, so this copy is UNENCRYPTED and readable by anyone who can read this folder or a backup of it.{Siblings:l}",
                        asidePath, siblings);
                    break;

                case FileHeadReading.OtherBytes:
                    Serilog.Log.Warning(
                        "[SqliteCipherHelper] Renamed the existing SQLite file to {Aside}. Its first 16 bytes were read and are NOT the plain SQLite header, so it is an encrypted or corrupt file the current key does not open — nothing here can read it, and nothing on this path deletes a copy it cannot read.{Siblings:l}",
                        asidePath, siblings);
                    break;

                case FileHeadReading.TooShort:
                    Serilog.Log.Warning(
                        "[SqliteCipherHelper] Renamed the existing SQLite file to {Aside}. It is shorter than the 16 bytes a database header needs, so its contents were NOT classified — that is the fail-safe answer and not a measurement of what is in it. Nothing on this path deletes a copy it has not measured as plain.{Siblings:l}",
                        asidePath, siblings);
                    break;

                default:
                    Serilog.Log.Warning(
                        "[SqliteCipherHelper] Renamed the existing SQLite file to {Aside}. Its first bytes could not be read at all, so NOTHING is known about its contents — that is the fail-safe answer and not a measurement. Nothing on this path deletes a copy it has not measured as plain.{Siblings:l}",
                        asidePath, siblings);
                    break;
            }
        }

        /// <summary>
        /// The sentence about the copy's siblings, which is a statement about the copy's
        /// COMPLETENESS and not a housekeeping note. Says nothing when there were none — a run
        /// that moved nothing must not be able to imply it preserved something.
        ///
        /// <para>⚠ A COPIED SIBLING IS NOT A RENAMED ONE and the sentence distinguishes them. A
        /// rename is atomic and takes the whole file; a copy off a sibling another process may
        /// still be writing is a read of a moving target. Both leave the pages beside the aside,
        /// and only one of them can be called a guaranteed snapshot, so only one of them is.</para>
        /// </summary>
        private static string DescribeSidecars(SidecarOutcome sidecars)
        {
            var beside = sidecars.Beside ?? Array.Empty<string>();
            var copied = sidecars.Copied ?? Array.Empty<string>();
            var lost = sidecars.Lost ?? Array.Empty<string>();

            if (beside.Length == 0 && lost.Length == 0) return string.Empty;

            var sentence = beside.Length == 0
                ? string.Empty
                : $" Its {beside.Length} -wal/-shm/-journal sibling(s) are beside it under its name, so the copy"
                  + " has them: rename all of them back together to recover it.";

            if (copied.Length > 0)
            {
                sentence += $" ⚠ {copied.Length} of those could not be renamed and were COPIED instead, so the"
                    + " original(s) may still be at the store's own path, and a copy taken from a file another"
                    + " process may still be writing is not a guaranteed snapshot of it.";
            }

            if (lost.Length > 0)
            {
                sentence += $" ⚠ {lost.Length} sibling(s) could be neither renamed nor copied beside the copy and"
                    + " are still at the store's own path (" + string.Join(", ", lost) + "), so the copy is missing"
                    + " whatever was in them — for a WAL-mode store that can be all of it. Nothing here deletes"
                    + " them, but re-opening a database at that path can, so move them beside the copy by hand now.";
            }

            return sentence;
        }

        /// <summary>
        /// The refusal, named. It says the file, the reason and the remedy, so an operator reading
        /// a service log knows what to move; the old path produced "malformed database schema"
        /// from a CREATE TABLE, which describes neither.
        /// </summary>
        private static IOException ReinitBlocked(string? blockingPath)
        {
            var message =
                $"SQLTriage could not re-initialise the encrypted store at '{blockingPath}'. The existing "
                + "file could not be renamed aside after " + ClearAttempts + " attempts, so it "
                + "is held by another process or denied by its permissions. Re-opening at the same path "
                + "would fail inside SQLCipher with an unrelated schema error, so it was not attempted. "
                + "Stop whatever holds the file (or move it aside by hand) and start again.";

            Serilog.Log.Error("[SqliteCipherHelper] {Message}", message);
            return new IOException(message);
        }

        /// <summary>Attempts made to clear the file before giving up. Each is preceded by a finaliser drain.</summary>
        private const int ClearAttempts = 4;

        /// <summary>Pause between clear attempts, long enough for a queued finaliser to run.</summary>
        private const int ClearRetryDelayMs = 120;

    }
}
#pragma warning restore CA1416
