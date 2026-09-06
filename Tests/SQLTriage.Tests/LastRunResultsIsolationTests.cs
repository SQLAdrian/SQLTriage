/* In the name of God, the Merciful, the Compassionate */

// D1 (gate ruling, 2026-08-05). The Audit Assessment page filled its grid from
// CheckExecutionService.GetResults(server, 1000). That method answers "the best current picture of
// this instance": it tops its hot cache (capped at CheckExecution:MaxResultsPerInstance, default 50)
// up from the persisted latest-run store and then from SQLite. Correct for a dashboard, wrong for a
// run — it hands a narrow run rows no part of that run executed.
//
// The measurement, staged below rather than recalled: with the 58-row previous run this file writes
// to the real store, GetResults answers 58 rows for an instance whose current run covered 13
// Encryption checks — 45 rows from the categories the coverage notice directly beneath them says
// did not run. (An earlier version of this comment cited "50 rows, 37 of them" from a live
// sequence that does not reproduce: the page clears the executor before every run, so the numbers
// a reader could reproduce are the ones the tests here stage.)
//
// The invariant these tests pin: after a run, the rows a caller shows are EXACTLY that run's rows,
// the accept/revoke overlay cannot widen them afterwards, and a category-filtered run does not
// overwrite the persisted latest-run record (Adrian's ruling, 2026-08-05).
//
// No SQL Server is reached. The union is reproduced through the persisted store, which is what
// actually supplied the foreign rows — a mock executor would have proved nothing about the seam
// that failed. The persistence tests DO call the executor, against a dead localhost port, because
// the write they are about happens at the end of a real run and nowhere else.
//
// Profile-independent: every member compiles in every edition.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class LastRunResultsIsolationTests : IDisposable
    {
        // A per-test instance name. The store keys files by server name, so a fixed one would let
        // two runs of this suite read each other's leftovers.
        private readonly string _server = "SQLT-TEST-LASTRUN-" + Guid.NewGuid().ToString("N")[..8];
        private readonly QuickCheckResultStore _store =
            new(NullLogger<QuickCheckResultStore>.Instance);

        // Every path this class touches is under here or under the test binary's own output dir.
        // ServerConnectionManager without an explicit path binds a real per-user location, which
        // RealUserProfileGuard refuses under a test host — correctly.
        private readonly string _tempDir =
            Path.Combine(Path.GetTempPath(), "sqlt-lastrun-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            // The store writes under the TEST BINARY's output/quickcheck, never a configured
            // install's — but it still gets cleaned, so a later run cannot read this one's files.
            try
            {
                foreach (var f in Directory.GetFiles(_store.RootDir, _server + "*.json"))
                    File.Delete(f);
            }
            catch { /* best-effort cleanup */ }
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        private CheckExecutionService NewExecutor()
        {
            var configuration = new ConfigurationBuilder().Build();
            Directory.CreateDirectory(_tempDir);
            var connections = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + "-connections.json");

            var connMgr = new ServerConnectionManager(
                NullLogger<ServerConnectionManager>.Instance, seats: null, connectionsFilePath: connections);
            var checkRepo = new CheckRepositoryService(
                NullLogger<CheckRepositoryService>.Instance, configuration);

            return new CheckExecutionService(
                NullLogger<CheckExecutionService>.Instance, checkRepo, connMgr, configuration,
                resultStore: _store);
        }

        private static CheckResult Row(string server, string checkId, string category) =>
            new() { InstanceName = server, CheckId = checkId, Category = category, CheckName = checkId };

        /// <summary>A wide previous run, as a completed full audit leaves on disk: four categories,
        /// well past the 50-row hot-cache cap so the top-up path is genuinely exercised.</summary>
        private List<CheckResult> WidePreviousRun() =>
            Enumerable.Range(1, 15).Select(i => Row(_server, $"CFG-{i}", "Configuration"))
            .Concat(Enumerable.Range(1, 15).Select(i => Row(_server, $"PRF-{i}", "Performance")))
            .Concat(Enumerable.Range(1, 15).Select(i => Row(_server, $"AUD-{i}", "Auditing")))
            .Concat(Enumerable.Range(1, 13).Select(i => Row(_server, $"ENC-{i}", "Encryption")))
            .ToList();

        [Fact]
        public void GetResults_UnionsThePersistedPreviousRun_WhichIsWhyItCannotFillARunsGrid()
        {
            // The positive control for the two tests below: without it, "GetLastRunResults returned
            // nothing" would pass vacuously on a store that never held anything.
            var executor = NewExecutor();
            _store.WriteRun(_server, WidePreviousRun());

            var viaCache = executor.GetResults(_server, 1000);

            viaCache.Should().HaveCount(58,
                "GetResults deliberately re-hydrates the last persisted run — that is its job, and "
                + "it is exactly why a RUN must not be rendered from it");
            viaCache.Should().Contain(r => r.Category == "Auditing");
        }

        [Fact]
        public void GetLastRunResults_ReturnsNothing_WhenThisProcessRanNothing()
        {
            // A wide run is on disk. No run has executed here. The honest answer to "what did this
            // run produce" is nothing — not the run somebody else finished earlier.
            var executor = NewExecutor();
            _store.WriteRun(_server, WidePreviousRun());

            executor.GetLastRunResults(_server).Should().BeEmpty();
        }

        [Fact]
        public void AnnotatingTheGridInPlace_AddsNoRows_SoAFilteredRunStaysFiltered()
        {
            // The accept/revoke path re-applies the acceptance overlay to the grid. It used to do
            // that by re-IMPORTING every server through GetResults, so one click on a filtered run
            // pulled the excluded categories back in underneath a notice saying they had not run.
            // An annotation may change what a row SAYS about itself; it may never change which rows
            // are on the grid.
            var executor = NewExecutor();
            _store.WriteRun(_server, WidePreviousRun());

            using var state = new QuickCheckStateService();
            state.Results.AddRange(
                Enumerable.Range(1, 13).Select(i => Row(_server, $"ENC-{i}", "Encryption")));
            state.CategoryExclusionNotice =
                "Category filter: Auditing, Configuration, Performance excluded from this run. "
                + "45 of the 58 enabled checks in the catalog did not run.";
            var before = state.Results.Select(r => r.CheckId).ToList();

            executor.AnnotateAcceptances(state.Results);

            state.Results.Should().HaveCount(13);
            state.Results.Select(r => r.CheckId).Should().Equal(before);
            state.Results.Should().OnlyContain(r => r.Category == "Encryption",
                "no row from an excluded category may reappear under a notice naming it excluded");
            state.CategoryExclusionNotice.Should().NotBeNull(
                "the sentence still describes the rows on the grid, so it stands");
        }

        [Fact]
        public void ClearAllResults_AlsoDropsTheLastRunRecord()
        {
            // The page clears the executor at the top of every run. If the last-run record survived
            // that, a run which then failed before executing anything would render the run before
            // it, under the new run's coverage sentence.
            var executor = NewExecutor();
            executor.ClearAllResults();

            executor.GetLastRunResults(_server).Should().BeEmpty();
        }

        [Fact]
        public void GetLastRunResults_OfABlankInstanceName_IsEmptyRatherThanAThrow()
        {
            NewExecutor().GetLastRunResults("").Should().BeEmpty();
        }

        // ── The latest-run record (Adrian's ruling, 2026-08-05) ──────────────
        // A category-filtered run stays on screen and exportable with its disclosure, and does NOT
        // become the server's persisted latest run: Checks, RemediationTuner, ComplianceMap/Tree
        // and /audit's cold start all read that record with no notion of coverage, so a narrow run
        // landing there is read as a full assessment. The write happens at the end of a real run,
        // so these exercise the real overload — with a catalog of method:host-probe checks and no
        // probe service wired, which is a SKIP-with-reason that opens no connection.

        /// <summary>A check that produces a result without reaching any server: host-probe with no
        /// HostProbeService in the executor is the documented SKIP-with-reason path.</summary>
        private static SqlCheck OfflineCheck(string id, string category) => new()
        {
            Id = id, Name = id, Category = category, Method = "host-probe", ProbeKey = "host.none",
        };

        private CheckExecutionService NewExecutorWithOfflineCatalog()
        {
            var configuration = new ConfigurationBuilder().Build();
            Directory.CreateDirectory(_tempDir);
            var connections = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + "-connections.json");

            var connMgr = new ServerConnectionManager(
                NullLogger<ServerConnectionManager>.Instance, seats: null, connectionsFilePath: connections);
            var checkRepo = new CheckRepositoryService(
                NullLogger<CheckRepositoryService>.Instance, configuration);
            checkRepo.AddCheck(OfflineCheck("ENC-1", "Encryption"));
            checkRepo.AddCheck(OfflineCheck("CFG-1", "Configuration"));

            return new CheckExecutionService(
                NullLogger<CheckExecutionService>.Instance, checkRepo, connMgr, configuration,
                resultStore: _store);
        }

        /// <summary>A connection object for the run. Nothing is opened on it: every check in the
        /// offline catalog branches to the host probe before any SQL resolution.</summary>
        private static ServerConnection OfflineConnection() => new()
        {
            Id = Guid.NewGuid().ToString(),
            ServerNames = "tcp:localhost,56599",   // nothing listens here
            UseWindowsAuthentication = true,
            ConnectionTimeout = 2,
            IsEnabled = true,
        };

        [Fact]
        public async Task ACategoryFilteredRun_LeavesThePreviousFullRunAsTheLatestRunOnDisk()
        {
            // The ruling, asserted through the real store: the record a later reader picks up is
            // still the full assessment, byte-identical in the file it came from.
            var executor = NewExecutorWithOfflineCatalog();
            _store.WriteRun(_server, WidePreviousRun());
            var fullRunFile = _store.GetLatestRunFile(_server);
            fullRunFile.Should().NotBeNull("the positive control: a full run is on disk to be overwritten");

            var summary = await executor.ExecuteChecksAsync(
                OfflineConnection(), _server, c => c.Category == "Encryption",
                persistRun: false);

            summary.TotalChecks.Should().Be(1, "the run really executed, so the write really was reachable");
            executor.GetLastRunResults(_server).Should().HaveCount(1,
                "the filtered run's rows are still on screen and exportable — only the record is withheld");

            _store.GetLatestRunFile(_server).Should().Be(fullRunFile);
            var latest = _store.ReadLatestRun(_server);
            latest.Should().HaveCount(58);
            latest.Should().Contain(r => r.Category == "Auditing",
                "the categories the filtered run excluded are still in the server's record of itself");
        }

        [Fact]
        public async Task AnUnfilteredPageRun_StillBecomesTheLatestRun()
        {
            // The other half of the ruling, and the reason the flag defaults to true: an ordinary
            // full assessment writes the record exactly as it always has.
            var executor = NewExecutorWithOfflineCatalog();
            _store.WriteRun(_server, WidePreviousRun());
            var fullRunFile = _store.GetLatestRunFile(_server);

            await executor.ExecuteChecksAsync(OfflineConnection(), _server, _ => true);

            // Asserted on CONTENT, not on the file name: the store stamps to the second, so a run
            // landing in the same second as the seed reuses the path and a name comparison would
            // pass or fail on timing rather than on behaviour.
            fullRunFile.Should().NotBeNull();
            var latest = _store.ReadLatestRun(_server);
            latest.Should().NotBeNull();
            latest!.Should().HaveCount(2);
            latest!.Select(r => r.CheckId).Should().BeEquivalentTo(new[] { "ENC-1", "CFG-1" });
        }

        [Fact]
        public async Task AQuickCheckRunnerStylePredicateRun_StillBecomesTheLatestRun()
        {
            // QuickCheckRunner narrows the SAME overload with a quick-check id set and must keep
            // persisting. That is why the skip keys on an explicit option off the page's category
            // selection and never on the presence of a filter delegate: this run is filtered, and
            // it writes.
            var executor = NewExecutorWithOfflineCatalog();
            _store.WriteRun(_server, WidePreviousRun());
            var quickIds = new HashSet<string>(new[] { "ENC-1" }, StringComparer.OrdinalIgnoreCase);

            await executor.ExecuteChecksAsync(OfflineConnection(), _server, c => quickIds.Contains(c.Id));

            _store.ReadLatestRun(_server).Should().ContainSingle()
                .Which.CheckId.Should().Be("ENC-1");
        }
    }
}
