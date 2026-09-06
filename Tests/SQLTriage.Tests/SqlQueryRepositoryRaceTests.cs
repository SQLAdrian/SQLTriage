/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;

namespace SQLTriage.Tests;

/// <summary>
/// Concurrency stress for <see cref="SqlQueryRepository"/>.
///
/// The defect these exercise: the repository used to publish a load by clearing the live
/// dictionary and then refilling it entry by entry. Any reader that sampled inside that window
/// saw a repository that was empty or half-filled — even though a load it had already awaited
/// had completed. The visible symptom was an intermittent Assert.NotNull failure in
/// SqlQueryRepositoryTests.Get_ExistingQuery_ReturnsDefinition, but the same window is open to
/// every DI consumer, because BundleStateChanged fires a reload on a background thread.
///
/// These tests are deliberately written against the repository's pre-fix public surface — none of
/// them touches InitializationComplete — so they can be pointed at the old implementation without
/// being edited. Doing so is how the reproduction was proved rather than assumed. To repeat it,
/// restore the pre-fix SqlQueryRepository.cs and add one line to it,
/// <c>public Task InitializationComplete { get; } = Task.CompletedTask;</c>: the tests do not need
/// the member, but the interface and two unrelated test doubles now declare it, so the solution
/// will not compile without it. That stub is inert and does not soften either defect.
///
/// Two mutants were run. The numbers below are counts of runs in which each test FAILED, i.e.
/// caught the defect:
///
///   against the pre-fix implementation (torn publish AND no load gate) —
///     ReloadStorm 10/10, ConstructThenReadImmediately 10/10, PublishedGeneration 9/10;
///   against a mutant keeping the atomic publish but deleting the load gate —
///     PublishedGeneration 15/15, and the other two passed every run, catching nothing.
///
/// That second row is the point: a torn publish and an out-of-order publish are different
/// failures, and the tests that catch the first are structurally blind to the second. See
/// PublishedGeneration_UnderConcurrentLoads_NeverGoesBackwards for why.
///
/// All figures are from this box, Debug, and are evidence that each test CAN fail — they are not
/// a probability that a future regression will be caught on any one run.
///
/// What they do NOT cover: the genuine startup window. The constructor starts its load on a
/// background thread and does not block, so a reader that runs before any load has finished sees
/// an EMPTY repository — by design. Every test below awaits a load first, so every assertion here
/// is about a reader that runs AFTER a completed load.
/// </summary>
public sealed class SqlQueryRepositoryRaceTests : IDisposable
{
    private const int FileCount = 40;
    private const string IdPrefix = "race-stress-";
    private const string MarkerId = "race-generation-marker";

    private readonly string _sqlDir;
    private readonly List<string> _tempFiles = new();

    public SqlQueryRepositoryRaceTests()
    {
        _sqlDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Sql");
        Directory.CreateDirectory(_sqlDir);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<List<string>> WriteStressFilesAsync()
    {
        var ids = new List<string>(FileCount);
        for (var i = 0; i < FileCount; i++)
        {
            var id = IdPrefix + i.ToString("D3");
            var path = Path.Combine(_sqlDir, id + ".sql");
            await File.WriteAllTextAsync(path, $"SELECT {i} AS n /* {new string('x', 512)} */");
            _tempFiles.Add(path);
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>Metadata giving every stress query the same category, so the tag index is populated.</summary>
    private static string BuildQueriesJson(IEnumerable<string> ids)
    {
        var entries = string.Join(",", ids.Select(id =>
            $"\"{id}\":{{\"category\":\"Stress\",\"severity\":\"HIGH\",\"status\":\"working\",\"quick\":true}}"));
        return $"{{\"queries\":{{{entries}}}}}";
    }

    private static SqlQueryRepository MakeRepo(IEnumerable<string> ids)
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/queries.json", BuildQueriesJson(ids));
        return new SqlQueryRepository(NullLogger<SqlQueryRepository>.Instance, bundle);
    }

    // ── Generation-marker helpers ────────────────────────────────────────────

    private string MarkerPath => Path.Combine(_sqlDir, MarkerId + ".sql");

    /// <summary>
    /// Replaces the marker file ATOMICALLY: writes a temp file (a .tmp extension, so the
    /// repository's "*.sql" glob never sees it half-written) and moves it over the marker.
    /// Atomicity is load-bearing here, not tidiness — a plain truncate-and-rewrite would let a
    /// concurrent load read a TRUNCATED generation number, which parses as a smaller number and
    /// would fake the exact backwards step this test treats as the defect.
    ///
    /// Returns false when the move loses a sharing race with a load that has the marker open;
    /// the caller retries with the next generation, so a lost race only slows the writer down.
    /// </summary>
    private static async Task<bool> TryWriteGenerationAsync(string markerPath, long generation)
    {
        var tmp = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, $"SELECT 1 AS n /* GEN={generation:D12} */");
            File.Move(tmp, markerPath, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>
    /// Reads the generation back out of published SQL text. Returns -1 when the marker is not
    /// parseable, which the caller counts separately rather than treating as a backwards step.
    /// </summary>
    private static long ParseGeneration(string sql)
    {
        const string token = "GEN=";
        var at = sql.IndexOf(token, StringComparison.Ordinal);
        if (at < 0) return -1;
        var start = at + token.Length;
        var end = start;
        while (end < sql.Length && char.IsAsciiDigit(sql[end])) end++;
        return end > start && long.TryParse(sql.AsSpan(start, end - start), out var g) ? g : -1;
    }

    // ── The stress tests ─────────────────────────────────────────────────────

    /// <summary>
    /// A reload storm running against readers that never stop reading. Every assertion is scoped
    /// to what a reader observes AFTER an awaited load has completed: from that point the
    /// repository must never report fewer queries than the load published, and no reader call
    /// may throw.
    /// </summary>
    [Fact]
    public async Task ReloadStorm_ReaderAfterCompletedLoad_NeverSeesAnEmptyOrShortRepository()
    {
        var ids = await WriteStressFilesAsync();
        var repo = MakeRepo(ids);

        // One completed load. Everything asserted below happens strictly after this returns.
        await repo.ReloadAsync();

        foreach (var id in ids)
            Assert.NotNull(repo.Get(id));

        var nullGets = 0;
        var shortGetAlls = 0;
        var emptyGetAlls = 0;
        var shortTagHits = 0;
        long reads = 0;
        var faults = new ConcurrentQueue<Exception>();

        var readersStop = new CancellationTokenSource();
        // Hard bound so a wedged reader can never hang the suite.
        var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var readers = Enumerable.Range(0, 3).Select(r => Task.Run(() =>
        {
            var probe = ids[r % ids.Count];
            while (!readersStop.IsCancellationRequested && !deadline.IsCancellationRequested)
            {
                try
                {
                    // Tight Get() loop: the publish window is microseconds wide, so the sample
                    // rate is what decides whether a reader lands inside it.
                    for (var k = 0; k < 64; k++)
                    {
                        if (repo.Get(probe) is null)
                            Interlocked.Increment(ref nullGets);
                    }

                    var all = repo.GetAll();
                    if (all.Count == 0) Interlocked.Increment(ref emptyGetAlls);
                    else if (all.Count < FileCount) Interlocked.Increment(ref shortGetAlls);

                    // GetByTag walks the tag index and resolves each id in the query dictionary —
                    // it is the reader that sees an index and a dictionary disagree.
                    if (repo.GetByTag("stress").Count < FileCount)
                        Interlocked.Increment(ref shortTagHits);

                    _ = repo.GetQuickChecks();
                    Interlocked.Increment(ref reads);
                }
                catch (Exception ex)
                {
                    faults.Enqueue(ex);
                }
            }
        })).ToArray();

        var sw = Stopwatch.StartNew();
        var writers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 60 && !deadline.IsCancellationRequested; i++)
                await repo.ReloadAsync();
        })).ToArray();

        await Task.WhenAll(writers);
        sw.Stop();
        readersStop.Cancel();
        await Task.WhenAll(readers);

        var detail =
            $"reads={Interlocked.Read(ref reads)} storm={sw.ElapsedMilliseconds}ms " +
            $"nullGets={nullGets} emptyGetAlls={emptyGetAlls} shortGetAlls={shortGetAlls} " +
            $"shortTagHits={shortTagHits} faults={faults.Count}" +
            (faults.TryDequeue(out var first) ? $" first={first.GetType().Name}: {first.Message}" : "");

        Assert.True(Interlocked.Read(ref reads) > 0, "readers never ran: " + detail);
        Assert.True(nullGets == 0, "Get() returned null after a completed load. " + detail);
        Assert.True(emptyGetAlls == 0, "GetAll() was empty after a completed load. " + detail);
        Assert.True(shortGetAlls == 0, "GetAll() lost entries after a completed load. " + detail);
        Assert.True(shortTagHits == 0, "GetByTag() lost entries after a completed load. " + detail);
        Assert.True(faults.IsEmpty, "a reader threw. " + detail);
    }

    /// <summary>
    /// The construct-and-read-immediately shape, repeated: the same sequence as the intermittent
    /// Get_ExistingQuery_ReturnsDefinition failure that was originally reported.
    ///
    /// Read this as the SYMPTOM reproduction, not as the regression guard. A reader only sees a
    /// torn publish if it samples inside a window microseconds wide, so this test's sensitivity is
    /// set by how often that window is open. Measured against the pre-fix implementation with ONE
    /// background reload per iteration, it missed the defect in 1 run of 8, and on the 7 runs it
    /// did catch it the miss count ranged from 296 to 1188 out of 48,000 reads — a real regression
    /// sat that close to going unnoticed. With three concurrent reloads it caught 10 of 10, the
    /// smallest margin being 185 misses.
    ///
    /// Even so, ReloadStorm above is the test to trust when publish atomicity regresses, and
    /// PublishedGeneration is the one to trust when load serialisation regresses. If this test
    /// ever needs deleting, those two are what must keep passing.
    /// </summary>
    [Fact]
    public async Task ConstructThenReadImmediately_UnderConcurrentReload_AlwaysResolvesTheQuery()
    {
        var ids = await WriteStressFilesAsync();
        var probe = ids[0];

        var misses = 0;
        const int iterations = 120;
        var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        for (var i = 0; i < iterations && !deadline.IsCancellationRequested; i++)
        {
            var repo = MakeRepo(ids);
            await repo.ReloadAsync();

            // Background reloads, exactly as BundleStateChanged raises them, while we read. Three
            // rather than one: each publish is a separate chance for the reader to land mid-write,
            // and one reload left the window shut often enough to miss a real regression.
            var background = Enumerable.Range(0, 3)
                .Select(_ => Task.Run(async () => await repo.ReloadAsync()))
                .ToArray();

            for (var k = 0; k < 400; k++)
            {
                if (repo.Get(probe) is null)
                    Interlocked.Increment(ref misses);
            }

            await Task.WhenAll(background);
        }

        Assert.True(misses == 0,
            $"Get() returned null {misses} time(s) across {iterations} construct-load-read iterations.");
    }

    /// <summary>
    /// The load gate's own test: two loads must not interleave such that the STALE one publishes
    /// last.
    ///
    /// Why the other two tests cannot see this. Both write the stress files once and then re-read
    /// that same unchanged set on every reload, so every generation of the dictionary is
    /// byte-identical. A stale load winning the publish race is indistinguishable from a fresh one
    /// winning it, and no assertion over identical content can separate them. That is structural,
    /// not bad luck — it is why removing the semaphore leaves them both green.
    ///
    /// So this test makes generations DISTINGUISHABLE: a writer keeps replacing one marker file
    /// with a strictly increasing generation number while loads storm. The invariant that follows
    /// is the gate's, and only the gate's:
    ///
    ///   a load reads the marker and publishes while HOLDING the gate, so for any two loads one
    ///   runs entirely before the other; the earlier one read an earlier-or-equal generation, and
    ///   published it earlier. The published generation is therefore non-decreasing over time.
    ///
    /// Note this needs no fairness assumption about SemaphoreSlim: it does not matter which waiter
    /// is granted the gate, only that read-and-publish is indivisible. Drop the gate and load A
    /// can read generation 5, load B read 7 and publish, and A then publish 5 over the top — a
    /// reader watching the marker sees 7 and then 5. That backwards step is what is counted here,
    /// and it is exactly the "stale generation wins" outcome the gate exists to prevent.
    /// </summary>
    [Fact]
    public async Task PublishedGeneration_UnderConcurrentLoads_NeverGoesBackwards()
    {
        var ids = await WriteStressFilesAsync();
        _tempFiles.Add(MarkerPath);
        Assert.True(await TryWriteGenerationAsync(MarkerPath, 0), "could not seed the generation marker");

        var allIds = new List<string>(ids) { MarkerId };
        var repo = MakeRepo(allIds);
        await repo.ReloadAsync();

        // The marker must be loadable at all, or every later sample is a vacuous skip.
        var seeded = repo.Get(MarkerId);
        Assert.NotNull(seeded);
        Assert.Equal(0, ParseGeneration(seeded!.Sql));

        long currentGen = 0;
        long writeCollisions = 0;
        long samples = 0;
        long advances = 0;
        long unparseable = 0;
        long absent = 0;
        var decreases = new ConcurrentQueue<string>();

        var stop = new CancellationTokenSource();
        // Hard bound so a wedged thread can never hang the suite.
        var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Writer: advances the generation as fast as the filesystem allows. The faster it moves,
        // the more distinguishable consecutive loads' reads become.
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && !deadline.IsCancellationRequested)
            {
                var next = Interlocked.Read(ref currentGen) + 1;
                if (await TryWriteGenerationAsync(MarkerPath, next))
                    Interlocked.Exchange(ref currentGen, next);
                else
                    Interlocked.Increment(ref writeCollisions);
            }
        });

        // Observers: each keeps its OWN last-seen generation. Per-thread monotonicity is the sound
        // check — a thread's successive reads are ordered in real time, and Volatile.Read inside
        // Get() gives each one acquire semantics — whereas a shared last-seen across threads would
        // report interleaving between observers as if it were interleaving between loads.
        var observers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            long lastSeen = -1;
            while (!stop.IsCancellationRequested && !deadline.IsCancellationRequested)
            {
                var def = repo.Get(MarkerId);
                if (def is null)
                {
                    // The marker was missing from this published snapshot: a load's read of it lost
                    // a sharing race with the writer's move. Counted, not asserted on.
                    Interlocked.Increment(ref absent);
                    continue;
                }

                var gen = ParseGeneration(def.Sql);
                if (gen < 0)
                {
                    Interlocked.Increment(ref unparseable);
                    continue;
                }

                if (gen < lastSeen)
                    decreases.Enqueue($"{lastSeen}->{gen}");
                else if (gen > lastSeen && lastSeen >= 0)
                    Interlocked.Increment(ref advances);
                lastSeen = gen;
                Interlocked.Increment(ref samples);
            }
        })).ToArray();

        // Load count is the sensitivity knob: a stale publish is a per-overlap event, so the
        // detection rate tracks how many loads overlap, not how hard the observers sample (they
        // already sample millions of times). Measured against the no-semaphore mutant, 4x80 loads
        // detected 12 runs out of 13; 6x150 detected every run of the 15 tried.
        var loaders = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 150 && !deadline.IsCancellationRequested; i++)
                await repo.ReloadAsync();
        })).ToArray();

        await Task.WhenAll(loaders);
        stop.Cancel();
        await Task.WhenAll(observers);
        await writer;

        var finalGen = Interlocked.Read(ref currentGen);
        var detail =
            $"finalGen={finalGen} samples={Interlocked.Read(ref samples)} " +
            $"advances={Interlocked.Read(ref advances)} decreases={decreases.Count} " +
            $"absent={Interlocked.Read(ref absent)} " +
            $"unparseable={Interlocked.Read(ref unparseable)} " +
            $"writeCollisions={Interlocked.Read(ref writeCollisions)}" +
            (decreases.TryDequeue(out var first) ? $" first={first}" : "");

        // Controls. Without these, "no decreases" could mean the race never ran: a run where the
        // writer never moved, or where the observers only ever saw one generation, would satisfy
        // monotonicity vacuously. `advances` is the load-bearing one — it counts observed FORWARD
        // steps, so it proves the observers watched the published generation actually change,
        // which is the only thing that makes "and never backwards" a measurement.
        Assert.True(Interlocked.Read(ref samples) > 0, "observers never sampled the marker. " + detail);
        Assert.True(finalGen > 0, "the writer never advanced the generation. " + detail);
        Assert.True(Interlocked.Read(ref advances) > 0,
            "observers never saw the published generation move, so monotonicity held vacuously. " + detail);
        Assert.True(Interlocked.Read(ref unparseable) == 0,
            "a published marker did not parse — the atomic-replace assumption broke. " + detail);

        Assert.True(decreases.IsEmpty,
            "a stale load published over a fresher one: the published generation went backwards. " + detail);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
        }

        // Sweep any .tmp left behind by a writer that lost its race at the moment of cancellation.
        try
        {
            foreach (var stray in Directory.GetFiles(_sqlDir, MarkerId + ".*.tmp"))
            {
                try { File.Delete(stray); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }
}
