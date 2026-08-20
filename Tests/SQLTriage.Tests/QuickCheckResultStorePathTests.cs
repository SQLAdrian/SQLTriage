/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Pins the run-file NAME matching in <see cref="QuickCheckResultStore"/>.
    ///
    /// The store used a bare "&lt;safe&gt;-*.json" glob, which is ambiguous between two servers
    /// whose safe names prefix one another: "SQL01-*.json" also matches every
    /// "SQL01-DR-&lt;stamp&gt;.json". Two consequences, both silent — SQL01's latest-run lookup
    /// could return SQL01-DR's file (so the --audit json export shipped one server's results
    /// labelled as another's), and SQL01's retention trim counted SQL01-DR's runs toward the
    /// 10-file cap and could delete them.
    ///
    /// Every test here uses a GUID-suffixed server name: the store's root directory is fixed at
    /// AppContext.BaseDirectory/output/quickcheck with no override, so uniqueness is what keeps
    /// these from colliding with each other or with ImportResultsServiceTests under a parallel run.
    /// </summary>
    public class QuickCheckResultStorePathTests : IDisposable
    {
        private readonly QuickCheckResultStore _store =
            new(NullLogger<QuickCheckResultStore>.Instance);

        private readonly List<string> _serverNamesToClean = new();

        public void Dispose()
        {
            foreach (var server in _serverNamesToClean)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(_store.RootDir, $"{server}-*.json"))
                        File.Delete(f);
                }
                catch { /* best-effort cleanup */ }
            }
        }

        private string Track(string name)
        {
            _serverNamesToClean.Add(name);
            return name;
        }

        private static List<CheckResult> OneResult(string checkId) => new()
        {
            new CheckResult { CheckId = checkId, Category = "Security", Severity = "High", Passed = true },
        };

        [Fact]
        public void PrefixSiblingServers_DoNotShareRunFiles()
        {
            var baseName = Track("SQL01x" + Guid.NewGuid().ToString("N")[..8]);
            var drName = Track(baseName + "-DR");

            var basePath = _store.WriteRun(baseName, OneResult("BASE"));
            var drPath = _store.WriteRun(drName, OneResult("DR"));

            Assert.NotNull(basePath);
            Assert.NotNull(drPath);

            // CONTROL: the two files really are in the prefix relationship the old glob confused —
            // without this, the assertions below could pass simply because the names never
            // overlapped and the test would not be exercising anything.
            Assert.StartsWith(baseName + "-", Path.GetFileName(drPath!), StringComparison.Ordinal);

            Assert.Equal(basePath, _store.GetLatestRunFile(baseName));
            Assert.Equal(drPath, _store.GetLatestRunFile(drName));

            // And the payloads follow the files, which is what the caller actually consumes.
            Assert.Equal("BASE", _store.ReadLatestRun(baseName)!.Single().CheckId);
            Assert.Equal("DR", _store.ReadLatestRun(drName)!.Single().CheckId);
        }

        [Fact]
        public void WriteRun_ReturnsThePathItWrote_AndGetLastWrittenPathAgrees()
        {
            var server = Track("SQL02x" + Guid.NewGuid().ToString("N")[..8]);

            var written = _store.WriteRun(server, OneResult("C1"));

            Assert.NotNull(written);
            Assert.True(File.Exists(written));
            Assert.Equal(written, _store.GetLastWrittenPath(server));
            Assert.Equal(written, _store.GetLatestRunFile(server));
        }

        [Fact]
        public void WriteRun_ReturnsNull_WhenThereIsNothingToWrite()
        {
            // CONTROL for the test above: the return value is not unconditionally a path.
            Assert.Null(_store.WriteRun("SQL03x" + Guid.NewGuid().ToString("N")[..8], new List<CheckResult>()));
            Assert.Null(_store.WriteRun("   ", OneResult("C1")));
        }

        [Fact]
        public void GetLatestRunFile_ReturnsNull_ForAServerWithNoRuns()
        {
            Assert.Null(_store.GetLatestRunFile("SQL04x" + Guid.NewGuid().ToString("N")[..8]));
        }

        [Fact]
        public void GetLastWrittenPath_ReturnsNull_ForAServerThisProcessNeverWrote()
        {
            // It reports only what THIS store instance wrote — it is not a disk lookup.
            var server = Track("SQL05x" + Guid.NewGuid().ToString("N")[..8]);
            _store.WriteRun(server, OneResult("C1"));

            var other = new QuickCheckResultStore(NullLogger<QuickCheckResultStore>.Instance);

            Assert.Null(other.GetLastWrittenPath(server));
            Assert.NotNull(other.GetLatestRunFile(server)); // ...but the file IS findable on disk
        }
    }
}
