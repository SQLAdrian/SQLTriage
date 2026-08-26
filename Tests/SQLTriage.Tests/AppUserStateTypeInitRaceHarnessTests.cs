/* In the name of God, the Merciful, the Compassionate */

// ── The AppUserState / BootstrapEligibilityProof circular type-init race, EXERCISED ─────────────
//
// ⚠⚠ WHY THIS FILE EXISTS. AppUserState's static constructor (Data/Services/AppUserState.cs,
// ~line 307-315) carries a doc comment stating a liveness hazard explicitly: it forces the nested
// BootstrapEligibilityProof type's initialiser via RuntimeHelpers.RunClassConstructor, and
// BootstrapEligibilityProof's own static ctor writes back to AppUserState's private static field
// — each type's initialiser touches the other. The comment says, in its own words, "no race
// harness has been written." This file is that harness.
//
// WHAT IS REAL HERE. A genuine CLR type-initialiser race: two threads, released simultaneously off
// a Barrier, one entering through AppUserState (the same entry BootstrapProofForTests.Steal() uses
// in this test suite), the other reaching BootstrapEligibilityProof directly (it is a public nested
// type; only its constructor is private, so no reflection or UnsafeAccessor is needed to name it).
// A type initialiser runs AT MOST ONCE PER LOAD CONTEXT, so the race can only be attempted once per
// process. This test spawns Tests/RaceHarness (AppUserStateRaceHarness.exe) as a FRESH CHILD
// PROCESS per attempt — the simplest and most faithful way to get a genuinely fresh CLR per
// iteration, over a collectible AssemblyLoadContext loading a second copy of a large WPF+Blazor
// assembly repeatedly. See Tests/RaceHarness/Program.cs for the attempt itself.
//
// THE SIGNAL. Bounded-wait liveness: the harness process joins both threads with a 3-second bound
// and prints a one-line VERDICT. OK = both threads finished (this attempt did not land in the
// race window). DEADLOCK = at least one thread did not complete inside the bound — the hazard the
// doc comment describes, REPRODUCED. This test spawns MANY attempts (in parallel, to keep wall
// time bounded — the window is documented as tight) and fails loudly, with the reproduction rate
// and the harness's own stdout as evidence, on the first DEADLOCK or ERROR verdict it observes.
//
// ⚠ NO EXISTING SLOW-TEST TRAIT/CATEGORY CONVENTION WAS FOUND IN THIS SUITE (grepped for
// [Trait(...)] and slow/category markers 2026-08-20; the nearest relative, LiveFactAttribute, gates
// on an external live SQL/AD environment, not on wall time, so it does not fit). This test spawns
// dozens of child processes and is genuinely slower than the suite's norm without being "live" in
// that sense, so it carries its own local marker below, on the class, rather than inventing a
// suite-wide one.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

[Trait("Speed", "Slow")]
public sealed class AppUserStateTypeInitRaceHarnessTests
{
    private readonly ITestOutputHelper _out;

    public AppUserStateTypeInitRaceHarnessTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Default attempt count for an ordinary run. Overridable with
    /// <c>SQLT_RACE_HARNESS_ITERATIONS</c> for a deeper hunt — the window is documented as tight,
    /// so a targeted investigation may want several thousand attempts, which this default does not
    /// spend on every routine run.
    /// </summary>
    private const int DefaultIterations = 150;

    private const string IterationsVariable = "SQLT_RACE_HARNESS_ITERATIONS";

    /// <summary>
    /// Walks up from the test assembly's own output directory to the repo root (identified by the
    /// harness project file, which only the repo root's subtree contains), the same technique
    /// AuditRestartBannerRenderTests.FindRepoRoot uses for the same reason: this must FAIL loudly
    /// if the source tree is not beside the running tests, rather than silently skip the race.
    /// </summary>
    private static string FindHarnessExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var projectPath = Path.Combine(dir.FullName, "Tests", "RaceHarness", "AppUserStateRaceHarness.csproj");
            if (File.Exists(projectPath))
            {
                // Debug carries no RuntimeIdentifier, so its output sits directly under the TFM
                // folder. Release mirrors SQLTriage.csproj's own Release-conditioned self-contained
                // shape (SelfContained + RuntimeIdentifier=win-x64, see the harness .csproj), which
                // adds a win-x64 subfolder — try both so this resolves regardless of which
                // configuration built the harness.
                foreach (var relativeSegments in new[]
                         {
                             new[] { "Debug", "net10.0-windows" },
                             new[] { "Release", "net10.0-windows", "win-x64" },
                         })
                {
                    var exePath = Path.Combine(
                        new[] { dir.FullName, "Tests", "RaceHarness", "bin" }
                            .Concat(relativeSegments)
                            .Append("AppUserStateRaceHarness.exe")
                            .ToArray());
                    if (File.Exists(exePath)) return exePath;
                }

                throw new InvalidOperationException(
                    $"Found {projectPath} but no built AppUserStateRaceHarness.exe beside it in " +
                    "Debug or Release. SQLTriage.Tests.csproj carries a ProjectReference to that " +
                    "project with ReferenceOutputAssembly=false specifically so `dotnet build`/" +
                    "`dotnet test` on this project also builds the harness; if that reference was " +
                    "removed, restore it rather than skipping this test.");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find Tests/RaceHarness/AppUserStateRaceHarness.csproj above " +
            AppContext.BaseDirectory + ". This test needs the harness source tree; it must FAIL " +
            "rather than pass silently if the tree it depends on is not present.");
    }

    private static int ResolvedIterations()
    {
        var raw = Environment.GetEnvironmentVariable(IterationsVariable);
        if (string.IsNullOrWhiteSpace(raw)) return DefaultIterations;
        return int.TryParse(raw, out var n) && n > 0 ? n : DefaultIterations;
    }

    private sealed record Attempt(int Index, int ExitCode, string StdOut, TimeSpan Elapsed);

    private static async Task<Attempt> RunOneAttemptAsync(string exePath, int index)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned null for {exePath}.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        // The harness itself enforces a 3s bound on its two threads and always returns; this
        // outer bound only guards against the CHILD PROCESS itself failing to exit (a process-level
        // hang one layer up from the thread-level hang the harness measures).
        var exited = await Task.Run(() => process.WaitForExit(10_000));
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        sw.Stop();

        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new Attempt(index, -1, stdOut + stdErr + "\n[PROCESS-LEVEL HANG: killed after 10s]", sw.Elapsed);
        }

        return new Attempt(index, process.ExitCode, stdOut + stdErr, sw.Elapsed);
    }

    /// <summary>
    /// THE RACE, ATTEMPTED MANY TIMES. Attempts run with bounded parallelism (process-spawn is the
    /// cost, not CPU, and each attempt's own barrier release is independent of every other attempt
    /// — different processes, different type-init state) so a meaningful attempt count fits in a
    /// reasonable wall time. Fails on the FIRST deadlock or error verdict, with the reproduction
    /// rate and the failing attempt's own stdout as evidence.
    /// </summary>
    [Fact]
    public async Task TypeInitRace_DoesNotDeadlock_AcrossManyFreshProcessAttempts()
    {
        var exePath = FindHarnessExe();
        var iterations = ResolvedIterations();
        var degreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

        _out.WriteLine($"Harness: {exePath}");
        _out.WriteLine($"Iterations: {iterations} (override with {IterationsVariable}); parallelism: {degreeOfParallelism}");

        var results = new List<Attempt>(iterations);
        var overallSw = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, iterations),
            new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism },
            async (i, ct) =>
            {
                var attempt = await RunOneAttemptAsync(exePath, i);
                lock (results) results.Add(attempt);
            });

        overallSw.Stop();
        _out.WriteLine($"Completed {results.Count} attempts in {overallSw.Elapsed}.");

        var deadlocks = results.Where(a => a.ExitCode != 0).OrderBy(a => a.Index).ToList();

        if (deadlocks.Count > 0)
        {
            var first = deadlocks[0];
            _out.WriteLine($"REPRODUCED on attempt {first.Index} (exit {first.ExitCode}):");
            _out.WriteLine(first.StdOut);
            _out.WriteLine(
                $"Reproduction rate: {deadlocks.Count}/{results.Count} " +
                $"({100.0 * deadlocks.Count / results.Count:F2}%).");
        }

        deadlocks.Should().BeEmpty(
            $"a non-zero exit means the harness's own bounded wait did not observe both threads " +
            $"complete (see the failing attempt's stdout in the test output above) — this is the " +
            $"circular type-initialiser deadlock AppUserState's static constructor documents as a " +
            $"read-but-unexercised hazard, now REPRODUCED. Per the task brief this is reported, not " +
            $"silently patched: the fix touches an RBAC security boundary and needs its own " +
            $"reviewed change.");
    }
}
