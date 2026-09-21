/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;

// SQLTriage.Data also defines a LogLevel; every sibling test file disambiguates the same way.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SQLTriage.Tests
{
    /// <summary>
    /// eval-failures-store-replace-fails (2026-09-10).
    ///
    /// <para><b>WHAT WAS WRONG.</b> The installed service logged 119 of these on 2026-09-09 alone,
    /// 00:01:27 to 18:54:45:</para>
    /// <code>
    /// [WRN] Could not persist evaluation-failure store to C:\SQLTriage-Service\Config\eval-failures.json
    /// System.IO.IOException: Unable to remove the file to be replaced.
    /// </code>
    /// <para>Win32 1175, ERROR_UNABLE_TO_REMOVE_REPLACED: something held the DESTINATION while
    /// <c>File.Replace</c> ran. They arrived in ADJACENT PAIRS 17–107 ms apart with ~300 s between
    /// clusters — a contention that clears in milliseconds, not a broken path — and the LATER attempts
    /// in the same cycle succeeded, so the old code was already demonstrating that a retry works, by
    /// accident, once per cycle.</para>
    ///
    /// <para><b>THE TWO THINGS THIS FILE PINS.</b>
    /// <list type="number">
    /// <item><b>Retry.</b> A transient failure is retried and the store still reaches disk, with no
    /// Warning — while a PERMANENT failure still surrenders after the attempt budget. A retry loop that
    /// hid a real failure forever would be worse than the noise it replaced.</item>
    /// <item><b>Coalescing.</b> <c>PersistFailures</c> runs once per FAILING ALERT+SERVER PAIR, not once
    /// per cycle: seven failing pairs rewrote the whole store seven times in a few milliseconds to
    /// publish one snapshot. Now a repeat tick on an episode already on disk defers to one flush at the
    /// end of the cycle — but an episode OPENING is still written synchronously, so a crash mid-cycle
    /// cannot lose a failure the panel is already showing.</item>
    /// </list></para>
    ///
    /// <para><b>Why the failures here are real file-handle failures.</b> The transient/permanent arms
    /// hold an actual <c>FileShare.Read</c> handle on the destination through
    /// <c>StoreWriteAttemptHookForTests</c>, so <c>File.Replace</c> throws the IOException the OS
    /// throws. The hook decides WHICH ATTEMPT is contended, which is what makes the retry pin
    /// deterministic instead of a sleep-and-hope; it never fabricates the exception. (MEASURED on this
    /// box while diagnosing the lane: a held <c>FileShare.Read</c> handle yields ERROR_SHARING_VIOLATION
    /// 32 and a full-sharing scanner handle yields no error at all, so 1175 itself could not be
    /// synthesised from user mode — it needs a concurrent destination-mutating operation. Both 32 and
    /// 1175 arrive as IOException and take the same retry path.)</para>
    /// </summary>
    public class EvalFailureStorePersistTests : IDisposable
    {
        private readonly string _tempDir;

        public EvalFailureStorePersistTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "eval-store-persist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        // ── 1: a transient failure is retried, and nobody hears about it ─────────────

        /// <summary>
        /// The live shape: attempt 1 collides, the contention clears, a later attempt succeeds. The
        /// store must end up on disk COMPLETE, and the log must stay clean — a retry that worked is not
        /// a failure. RED before the fix: one attempt, one Warning, and the second failure never
        /// reaches disk.
        /// </summary>
        [Fact]
        public void ATransientReplaceFailure_isRetried_theStoreReachesDisk_andNothingWarns()
        {
            var storePath = Path.Combine(NewDir("transient"), "eval-failures.json");
            var log = new CapturingLogger();
            using var svc = BuildEngine(storePath, log);

            // First episode creates the store, so the retried write below takes the File.Replace
            // branch (the branch that actually failed live), not the File.Move one.
            svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "unreachable");
            Assert.Single(ReadStore(storePath));

            FileStream? held = null;
            try
            {
                svc.StoreWriteAttemptHookForTests = attempt =>
                {
                    if (attempt == 1)
                        held = new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    else { held?.Dispose(); held = null; }
                };

                var before = svc.StoreWriteCount;
                svc.RecordEvaluationFailure("a2:srv2", "a2", "srv2", "also unreachable");

                Assert.Equal(before + 1, svc.StoreWriteCount);           // exactly one physical write landed
                Assert.Equal(2, ReadStore(storePath).Count);             // and it carried BOTH episodes
                Assert.Empty(PersistWarnings(log));                      // a retry that worked never warns
                Assert.Contains(log.At(LogLevel.Debug),
                    t => t.Contains("on attempt 2 of", StringComparison.Ordinal));
            }
            finally { svc.StoreWriteAttemptHookForTests = null; held?.Dispose(); }
        }

        // ── 2: a permanent failure still surrenders ─────────────────────────────────

        /// <summary>
        /// The retry must not become a way to never admit a fault. With the destination held for every
        /// attempt, the persist tries exactly <c>StoreWriteAttempts</c> times, gives up, and says so
        /// ONCE — with the exception attached, so the cause is nameable.
        /// </summary>
        [Fact]
        public void APermanentReplaceFailure_triesTheWholeBudget_thenSurrendersWithOneWarning()
        {
            var storePath = Path.Combine(NewDir("permanent"), "eval-failures.json");
            var log = new CapturingLogger();
            using var svc = BuildEngine(storePath, log);

            svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "unreachable");
            var onDiskBefore = File.ReadAllText(storePath);

            var attempts = new List<int>();
            FileStream? held = null;
            try
            {
                held = new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                svc.StoreWriteAttemptHookForTests = attempts.Add;

                var before = svc.StoreWriteCount;
                svc.RecordEvaluationFailure("a2:srv2", "a2", "srv2", "also unreachable");

                Assert.Equal(AlertEvaluationService.StoreWriteAttempts, attempts.Count);
                Assert.Equal(before, svc.StoreWriteCount);               // nothing landed
                Assert.Equal(onDiskBefore, File.ReadAllText(storePath)); // and the old store is intact

                var warnings = PersistWarnings(log);
                Assert.Single(warnings);
                Assert.Contains("Could not persist evaluation-failure store", warnings[0].Text, StringComparison.Ordinal);
                Assert.IsAssignableFrom<IOException>(warnings[0].Exception);
            }
            finally { svc.StoreWriteAttemptHookForTests = null; held?.Dispose(); }
        }

        // ── 3: episode cardinality — one Warning per EPISODE ────────────────────────

        /// <summary>
        /// Three failing persists in one unwritable episode are ONE Warning and two Debug repeats — the
        /// same cardinality <c>RecordEvaluationFailure</c> already obeys for the failure itself. One
        /// Warning per attempt (12 lines here) or per cycle is the identical unreadability defect
        /// wearing a different hat. When the store becomes writable again it says so exactly once, and
        /// the NEXT unwritable episode warns again — a latch would silence the store for the life of
        /// the process.
        /// </summary>
        [Fact]
        public void AnUnwritableEpisode_warnsOnce_repeatsAtDebug_recoversOnce_andTheNextEpisodeWarnsAgain()
        {
            var storePath = Path.Combine(NewDir("episode"), "eval-failures.json");
            var log = new CapturingLogger();
            using var svc = BuildEngine(storePath, log);

            svc.RecordEvaluationFailure("a0:srv0", "a0", "srv0", "unreachable");

            FileStream? held = null;
            try
            {
                held = new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                for (var i = 1; i <= 3; i++)
                    svc.RecordEvaluationFailure($"a{i}:srv{i}", $"a{i}", $"srv{i}", "unreachable");

                Assert.Single(PersistWarnings(log));
                Assert.Equal(2, log.At(LogLevel.Debug)
                    .Count(t => t.Contains("Still could not persist", StringComparison.Ordinal)));

                held.Dispose(); held = null;

                // Recovery: one Information line, once.
                svc.RecordEvaluationFailure("a4:srv4", "a4", "srv4", "unreachable");
                Assert.Single(log.At(LogLevel.Information)
                    .Where(t => t.Contains("is writable again", StringComparison.Ordinal)));

                // A new unwritable episode is a new Warning, not a swallowed repeat.
                held = new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                svc.RecordEvaluationFailure("a5:srv5", "a5", "srv5", "unreachable");
                Assert.Equal(2, PersistWarnings(log).Count);
            }
            finally { held?.Dispose(); }
        }

        // ── 4: coalescing — the write count is the claim, so measure it ─────────────

        /// <summary>
        /// The live banner showed SEVEN failing alert+server pairs, and every one of them rewrote the
        /// whole store. A second cycle over the same seven ongoing episodes changes only their counts
        /// and timestamps, so it must cost ONE write at the end of the cycle, not seven inside it.
        /// RED before the fix: seven.
        /// </summary>
        [Fact]
        public void SevenOngoingFailuresInOneCycle_costOneStoreWrite_notSeven()
        {
            var storePath = Path.Combine(NewDir("coalesce"), "eval-failures.json");
            using var svc = BuildEngine(storePath, new CapturingLogger());

            // Cycle 1: seven episodes OPEN. Each opening is a set change, so each is written at once —
            // that is the durability contract, not a regression.
            for (var i = 0; i < 7; i++)
                svc.RecordEvaluationFailure($"a{i}:srv{i}", $"a{i}", $"srv{i}", "unreachable");
            Assert.Equal(7L, svc.StoreWriteCount);
            Assert.Equal(7, ReadStore(storePath).Count);

            // Cycle 2: the same seven fail again. Nothing about WHICH failures exist has changed.
            var before = svc.StoreWriteCount;
            for (var i = 0; i < 7; i++)
                svc.RecordEvaluationFailure($"a{i}:srv{i}", $"a{i}", $"srv{i}", "still unreachable");
            Assert.Equal(before, svc.StoreWriteCount);   // not one write yet

            svc.FlushFailureStore();
            Assert.Equal(before + 1, svc.StoreWriteCount);

            // And the deferred refresh really reached disk — coalescing is not dropping.
            var rows = ReadStore(storePath);
            Assert.Equal(7, rows.Count);
            Assert.All(rows, r => Assert.Equal(2, r.FailureCount));
            Assert.All(rows, r => Assert.Equal("still unreachable", r.ErrorSummary));

            // A flush with nothing deferred is free.
            var settled = svc.StoreWriteCount;
            svc.FlushFailureStore();
            Assert.Equal(settled, svc.StoreWriteCount);
        }

        // ── 5: the durability contract the coalescing must not break ────────────────

        /// <summary>
        /// The hard constraint on coalescing: a crash mid-cycle must not lose a failure the panel is
        /// already showing. The "crash" here is the honest one — the service is NEVER flushed and NEVER
        /// disposed, and a fresh instance reads the same path, exactly as a restarted process does.
        /// </summary>
        [Fact]
        public void AnOpeningEpisode_isOnDiskBeforeTheCycleEnds_soACrashCannotLoseIt()
        {
            var storePath = Path.Combine(NewDir("crash"), "eval-failures.json");
            var svc = BuildEngine(storePath, new CapturingLogger());   // deliberately not disposed

            svc.RecordEvaluationFailure("io_err:srv9", "io_err", "srv9", "unreachable");
            Assert.Equal(1L, svc.StoreWriteCount);

            using var afterRestart = BuildEngine(storePath, new CapturingLogger());
            var f = Assert.Single(afterRestart.EvaluationFailures);
            Assert.Equal("io_err", f.AlertId);
            Assert.Equal("srv9", f.ServerName);
            Assert.True(afterRestart.HasEvaluationFailure("srv9"));
        }

        /// <summary>
        /// The other half of the set contract: a RECOVERY is a set change too, so it is written at
        /// once. Deferring it would let a restart resurrect a failure the panel had already cleared.
        /// </summary>
        [Fact]
        public void ARecovery_isOnDiskImmediately_soARestartCannotResurrectIt()
        {
            var storePath = Path.Combine(NewDir("recovery"), "eval-failures.json");
            var svc = BuildEngine(storePath, new CapturingLogger());   // deliberately not disposed

            svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "unreachable");
            svc.ClearEvaluationFailure("a1:srv1");

            using var afterRestart = BuildEngine(storePath, new CapturingLogger());
            Assert.Empty(afterRestart.EvaluationFailures);
        }

        /// <summary>
        /// An orderly shutdown is the other end-of-cycle. Whatever the last cycle deferred goes out on
        /// Dispose, so a stop between cycles costs nothing at all.
        /// </summary>
        [Fact]
        public void Dispose_flushesWhateverTheLastCycleDeferred()
        {
            var storePath = Path.Combine(NewDir("dispose"), "eval-failures.json");
            var svc = BuildEngine(storePath, new CapturingLogger());
            svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "unreachable");
            svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "still unreachable");   // deferred
            Assert.Equal(1, ReadStore(storePath).Single().FailureCount);

            svc.Dispose();

            var row = ReadStore(storePath).Single();
            Assert.Equal(2, row.FailureCount);
            Assert.Equal("still unreachable", row.ErrorSummary);
        }

        // ── 6: what must NOT be retried ─────────────────────────────────────────────

        /// <summary>
        /// Only contention is retried. A permissions fault cannot clear in 260 ms, so retrying it would
        /// buy nothing and delay the log; it surrenders on the first attempt, still once per episode.
        /// </summary>
        [Fact]
        public void ANonIoFault_isNotRetried_andStillWarnsOnce()
        {
            var storePath = Path.Combine(NewDir("nonio"), "eval-failures.json");
            var log = new CapturingLogger();
            using var svc = BuildEngine(storePath, log);

            var attempts = 0;
            try
            {
                svc.StoreWriteAttemptHookForTests = _ =>
                {
                    attempts++;
                    throw new UnauthorizedAccessException("Access to the path is denied.");
                };
                svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "unreachable");

                Assert.Equal(1, attempts);
                var warnings = PersistWarnings(log);
                Assert.Single(warnings);
                Assert.IsType<UnauthorizedAccessException>(warnings[0].Exception);
            }
            finally { svc.StoreWriteAttemptHookForTests = null; }
        }

        // ── 7: I1 — DryRun persists NOTHING, on EVERY path that can write ───────────

        /// <summary>
        /// <b>The defect this pins (fix round 1, 2026-09-10).</b> <c>RecordEvaluationFailure</c> and
        /// <c>ClearEvaluationFailure</c> both tested <c>_dryRun</c> before persisting, but the FLUSH
        /// this lane added did not — so a dirty flag carried out of a real cycle was written to disk by
        /// the next flush after DryRun was switched on, which is exactly what the DryRun property says
        /// cannot happen ("nothing is persisted"). Found by the cold gate by PROBING (G3), not by
        /// reading; the guard now sits at the one physical write, so a path added later inherits it.
        ///
        /// <para>Both product flush call sites are driven here — <c>FlushFailureStore</c>, which the
        /// finally of <c>EvaluateAllAsync</c> calls, and <c>Dispose</c> — and both an opening episode
        /// and a recovery are attempted while dry-running. The assertion is on the FILE, byte for byte,
        /// not on an intention.</para>
        /// </summary>
        [Fact]
        public void DryRun_persistsNothing_notEvenTheDeferredFlushOrDispose()
        {
            var storePath = Path.Combine(NewDir("dryrun"), "eval-failures.json");
            var svc = BuildEngine(storePath, new CapturingLogger());

            // A REAL cycle first: the episode opens and is written, then a repeat defers and leaves
            // the store dirty. This is the state the gate's probe carried into the dry run.
            svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "unreachable");
            svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "still unreachable");
            var onDiskBefore = File.ReadAllText(storePath);
            Assert.Equal(1, ReadStore(storePath).Single().FailureCount);

            svc.DryRun = true;

            var before = svc.StoreWriteCount;
            svc.RecordEvaluationFailure("a2:srv2", "a2", "srv2", "opened during the dry run");
            svc.ClearEvaluationFailure("a1:srv1");
            svc.FlushFailureStore();
            svc.Dispose();

            Assert.Equal(before, svc.StoreWriteCount);
            Assert.Equal(onDiskBefore, File.ReadAllText(storePath));
        }

        // ── 8: I2 — a write that did not land leaves the store dirty ────────────────

        /// <summary>
        /// Gate probe G6, taken as the gate wrote it (2026-09-10): GREEN on this lane's source and RED
        /// on the mutant that clears <c>_storeDirty</c> even when the write FAILED. That mutant survived
        /// all 36 of this lane's original tests, which left the commit's fourth claim — the snapshot is
        /// taken inside the lock so a write can never clear the flag for a change it did not persist —
        /// entirely unpinned. The destination is held by a REAL <c>FileShare.Read</c> handle for the
        /// whole retry budget, so the surrender is the OS's and not a fabricated one.
        /// </summary>
        [Fact]
        public void ASurrenderedFlush_keepsTheStoreDirty_soTheNextFlushStillPublishesIt()
        {
            var storePath = Path.Combine(NewDir("surrendered-flush"), "eval-failures.json");
            using var svc = BuildEngine(storePath, new CapturingLogger());

            svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "one");
            Assert.Equal(1, ReadStore(storePath).Single().FailureCount);

            svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "two");   // deferred

            using (new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var before = svc.StoreWriteCount;
                svc.FlushFailureStore();
                Assert.Equal(before, svc.StoreWriteCount);                 // the deferred flush surrendered
            }

            var atSecond = svc.StoreWriteCount;
            svc.FlushFailureStore();
            Assert.Equal(atSecond + 1, svc.StoreWriteCount);

            var row = ReadStore(storePath).Single();
            Assert.Equal(2, row.FailureCount);
            Assert.Equal("two", row.ErrorSummary);
        }

        /// <summary>
        /// The same hole reached through an OPENING episode (gate probe G1). The synchronous write
        /// surrendered and left the flag clean, so the end-of-cycle flush was a no-op and the failure
        /// was STILL absent from disk after Dispose — one row on disk against two in memory, a failure
        /// the panel was already showing that a restart would not have seen. It must be rescheduled.
        /// </summary>
        [Fact]
        public void ASurrenderedOpeningWrite_isRescheduled_soTheEndOfCycleFlushStillLandsIt()
        {
            var storePath = Path.Combine(NewDir("surrendered-open"), "eval-failures.json");
            using var svc = BuildEngine(storePath, new CapturingLogger());

            svc.RecordEvaluationFailure("a0:srv0", "a0", "srv0", "first");
            Assert.Single(ReadStore(storePath));

            using (new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var before = svc.StoreWriteCount;
                svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "opened while unwritable");
                Assert.Equal(before, svc.StoreWriteCount);                 // the opening write surrendered
            }

            svc.FlushFailureStore();

            var onDisk = ReadStore(storePath);
            Assert.Equal(2, onDisk.Count);
            Assert.Contains(onDisk, r => r.AlertId == "a1");
            Assert.Equal(svc.EvaluationFailures.Count(), onDisk.Count);    // disk and memory agree again
        }

        /// <summary>
        /// The recovery side, and the sharper one (gate probe G5). The clear removed the row from
        /// memory, its write surrendered, nothing marked the store dirty — so the row stayed on disk and
        /// a RESTART reloaded a failure that no longer existed. That is precisely the resurrection
        /// <see cref="ARecovery_isOnDiskImmediately_soARestartCannotResurrectIt"/> forbids, reached by a
        /// path that test does not walk: it asserts the happy path, where the write lands.
        /// </summary>
        [Fact]
        public void ASurrenderedRecovery_isRescheduled_soARestartStillCannotResurrectIt()
        {
            var storePath = Path.Combine(NewDir("surrendered-recovery"), "eval-failures.json");
            var svc = BuildEngine(storePath, new CapturingLogger());

            svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "unreachable");
            Assert.Single(ReadStore(storePath));

            using (new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var before = svc.StoreWriteCount;
                svc.ClearEvaluationFailure("a1:srv1");
                Assert.Equal(before, svc.StoreWriteCount);                 // the recovery write surrendered
            }
            Assert.Empty(svc.EvaluationFailures);

            svc.Dispose();                                                 // the orderly end-of-cycle

            Assert.Empty(ReadStore(storePath));
            using var afterRestart = BuildEngine(storePath, new CapturingLogger());
            Assert.Empty(afterRestart.EvaluationFailures);
        }

        /// <summary>
        /// The question rescheduling has to answer: now that every later flush retries a surrendered
        /// write, does one Warning become one per flush? It does not — the cardinality is owned by the
        /// episode latch, not by the dirty flag. Four persists against a destination held throughout are
        /// ONE Warning and three Debug repeats, and each of the four really did spend the whole attempt
        /// budget, so this measures a bounded retry and not a silent give-up.
        /// </summary>
        [Fact]
        public void APermanentlyUnwritableStore_isRetriedByEveryFlush_butStillWarnsOnlyOnce()
        {
            var storePath = Path.Combine(NewDir("no-spin"), "eval-failures.json");
            var log = new CapturingLogger();
            var svc = BuildEngine(storePath, log);

            svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "unreachable");
            Assert.Empty(PersistWarnings(log));

            var attempts = 0;
            FileStream? held = null;
            try
            {
                held = new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                svc.StoreWriteAttemptHookForTests = _ => attempts++;

                svc.RecordEvaluationFailure("a2:srv2", "a2", "srv2", "opened while unwritable");
                for (var i = 0; i < 3; i++) svc.FlushFailureStore();

                Assert.Equal(4 * AlertEvaluationService.StoreWriteAttempts, attempts);
                Assert.Single(PersistWarnings(log));
                Assert.Equal(3, log.At(LogLevel.Debug)
                    .Count(t => t.Contains("Still could not persist", StringComparison.Ordinal)));
            }
            finally { svc.StoreWriteAttemptHookForTests = null; held?.Dispose(); svc.Dispose(); }
        }

        // ── 9: I3 — the coalescing is wired into a REAL cycle, not just into the helper ──

        /// <summary>
        /// <b>Why this test exists (fix round 1, 2026-09-10).</b> Deleting the single line
        /// <c>FlushFailureStore();</c> from the <c>finally</c> of <c>EvaluateAllAsync</c> left all 36 of
        /// this lane's original tests GREEN, because every one of them called <c>FlushFailureStore()</c>
        /// directly and not one of them through a cycle. That line is the whole wiring of the coalescing
        /// into production: without it a deferred refresh reaches disk only at shutdown, and "a crash
        /// costs at most one cycle of precision" silently becomes the whole uptime. So the flush is
        /// driven here by the PUBLIC production entry point instead.
        ///
        /// <para>The engine is given a connections store that does not exist, so the cycle has no
        /// enabled servers and returns before <c>ReconcileEvaluationFailures</c> — nothing else inside
        /// the cycle can touch the store, and the one physical write asserted below can only have come
        /// from the finally. (A <c>return</c> inside a <c>try</c> still runs its <c>finally</c>; that is
        /// the property being pinned.) The other product call site, <c>Dispose</c>, is pinned by
        /// <see cref="Dispose_flushesWhateverTheLastCycleDeferred"/>.</para>
        /// </summary>
        [Fact]
        public async Task ARealEvaluationCycle_flushesWhatThatCycleDeferred()
        {
            var dir = NewDir("cycle");
            var storePath = Path.Combine(dir, "eval-failures.json");
            var connections = new ServerConnectionManager(
                NullLogger<ServerConnectionManager>.Instance,
                connectionsFilePath: Path.Combine(dir, "no-such-server-connections.json"));
            Assert.Empty(connections.GetEnabledConnections());

            var svc = BuildEngine(storePath, new CapturingLogger(), connections);
            try
            {
                svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "unreachable");
                svc.RecordEvaluationFailure("a1:srv1", "a1", "srv1", "still unreachable");   // deferred
                Assert.Equal(1, ReadStore(storePath).Single().FailureCount);

                var before = svc.StoreWriteCount;
                await svc.EvaluateAllAsync();

                Assert.Equal(before + 1, svc.StoreWriteCount);
                var row = ReadStore(storePath).Single();
                Assert.Equal(2, row.FailureCount);
                Assert.Equal("still unreachable", row.ErrorSummary);

                // And a cycle with nothing deferred costs no write at all — the coalescing is a
                // saving, not a rescheduling of the same seven writes.
                var settled = svc.StoreWriteCount;
                await svc.EvaluateAllAsync();
                Assert.Equal(settled, svc.StoreWriteCount);
            }
            finally { svc.Dispose(); }
        }

        // ── helpers ─────────────────────────────────────────────────────────────────

        private static List<(string Text, Exception? Exception)> PersistWarnings(CapturingLogger log) =>
            log.Lines(LogLevel.Warning)
               .Where(l => l.Text.Contains("Could not persist evaluation-failure store", StringComparison.Ordinal))
               .ToList();

        private string NewDir(string tag)
        {
            var d = Path.Combine(_tempDir, tag);
            Directory.CreateDirectory(d);
            return d;
        }

        private static List<AlertEvalFailure> ReadStore(string path)
        {
            Assert.True(File.Exists(path), "the durable failure store must have been written to disk");
            return JsonSerializer.Deserialize<List<AlertEvalFailure>>(File.ReadAllText(path)) ?? new();
        }

        private static AlertEvaluationService BuildEngine(
            string storePath, ILogger<AlertEvaluationService> logger, ServerConnectionManager? connections = null)
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);

            return new AlertEvaluationService(
                logger,
                new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                connections ?? new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                channels,
                new liveQueriesCacheStore(),
                new InlineOrchestrator(),
                evalFailureStorePath: storePath);
        }

        /// <summary>Keeps the EXCEPTION as well as the text: "warns once" is only half the contract,
        /// the other half is that the Warning names the cause.</summary>
        private sealed class CapturingLogger : ILogger<AlertEvaluationService>
        {
            private readonly List<(LogLevel Level, string Text, Exception? Exception)> _lines = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => Scope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_lines) _lines.Add((logLevel, formatter(state, exception), exception));
            }

            public List<string> At(LogLevel level)
            {
                lock (_lines) return _lines.Where(l => l.Level == level).Select(l => l.Text).ToList();
            }

            public List<(string Text, Exception? Exception)> Lines(LogLevel level)
            {
                lock (_lines)
                    return _lines.Where(l => l.Level == level).Select(l => (l.Text, l.Exception)).ToList();
            }

            private sealed class Scope : IDisposable
            {
                public static readonly Scope Instance = new();
                public void Dispose() { }
            }
        }

        /// <summary>Runs the work inline, exactly as the real orchestrator does on the happy path.</summary>
        private sealed class InlineOrchestrator : IQueryOrchestrator
        {
            public async Task<QueryResult> EnqueueAsync(
                QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default)
            {
                try
                {
                    await request.Work(cancellationToken);
                    return new QueryResult { QueryId = request.QueryId, Success = true };
                }
                catch (Exception ex)
                {
                    return new QueryResult { QueryId = request.QueryId, Success = false, Exception = ex };
                }
            }

            public Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorHealth());
            public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorMetrics());
            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
