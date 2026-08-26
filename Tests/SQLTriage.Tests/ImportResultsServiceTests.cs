/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// #84 results-import round-trip: a small RunPayload JSON (the shape
    /// QuickCheckResultStore.WriteRun itself writes) parses, validates, enriches missing
    /// narrative/costing fields from a stub local corpus, and lands in the store — exactly the
    /// path Pages/ImportResults.razor and --import drive.
    /// </summary>
    public class ImportResultsServiceTests : IDisposable
    {
        private readonly QuickCheckResultStore _store;
        private readonly CheckRepositoryService _checkRepo;
        private readonly ImportResultsService _importer;
        private readonly List<string> _serverNamesToClean = new();

        public ImportResultsServiceTests()
        {
            _store = new QuickCheckResultStore(NullLogger<QuickCheckResultStore>.Instance);

            _checkRepo = new CheckRepositoryService(
                NullLogger<CheckRepositoryService>.Instance,
                configuration: null,
                bundle: null);
            SeedCorpus(_checkRepo, new List<SqlCheck>
            {
                new SqlCheck
                {
                    Id = "CHK-100",
                    Name = "Page Life Expectancy",
                    Category = "Memory",
                    Severity = "Warning",
                    Description = "Corpus description for CHK-100",
                    RecommendedAction = "Corpus recommended action for CHK-100",
                    BusinessImpact = "Corpus business impact for CHK-100",
                    Eli5Description = "Corpus ELI5 description",
                    Eli5Remediation = "Corpus ELI5 remediation",
                    EffortHours = 2.5,
                    ScoreWeight = 3,
                },
            });

            _importer = new ImportResultsService(_store, _checkRepo, NullLogger<ImportResultsService>.Instance);
        }

        private static void SeedCorpus(CheckRepositoryService repo, List<SqlCheck> checks)
        {
            var field = typeof(CheckRepositoryService).GetField("_checks",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field!.SetValue(repo, checks);
        }

        private static string UniqueServer(string tag) => $"ImportTest-{tag}-{Guid.NewGuid():N}";

        private void TrackForCleanup(string serverName) => _serverNamesToClean.Add(serverName);

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

        [Fact]
        public void ImportFile_RoundTrips_AndEnrichesMissingFields()
        {
            var server = UniqueServer("roundtrip");
            TrackForCleanup(server);

            var payload = new QuickCheckResultStore.RunPayload
            {
                ServerName = server,
                WrittenAtUtc = DateTime.UtcNow,
                SchemaVersion = 1,
                Results = new List<CheckResult>
                {
                    new CheckResult
                    {
                        CheckId = "CHK-100",
                        CheckName = "Page Life Expectancy",
                        Category = "Memory",
                        Severity = "Warning",
                        Passed = false,
                        ActualValue = 120,
                        ExpectedValue = 300,
                        Message = "PLE below threshold",
                        InstanceName = server,
                        // Narrative/costing fields deliberately absent — the redacted-capture case.
                        Description = null,
                        RecommendedAction = null,
                        BusinessImpact = null,
                        Eli5Description = null,
                        Eli5Remediation = null,
                        EffortHours = 0,
                        ScoreWeight = 0,
                    },
                },
            };
            var json = JsonSerializer.Serialize(payload);

            var outcome = _importer.ImportFile("roundtrip.json", json);

            Assert.True(outcome.Success, outcome.Error);
            Assert.Null(outcome.Error);
            Assert.Equal(server, outcome.ServerName);
            Assert.Equal(1, outcome.ResultCount);
            Assert.Equal(1, outcome.EnrichedCount);
            Assert.Equal(0, outcome.UnenrichedCount);

            // Round-trip: the store now serves this run back out, enriched.
            var landed = _store.ReadLatestRun(server);
            Assert.NotNull(landed);
            var result = Assert.Single(landed!);
            Assert.Equal("CHK-100", result.CheckId);
            Assert.False(result.Passed); // truthful outcome untouched by enrichment
            Assert.Equal("Corpus description for CHK-100", result.Description);
            Assert.Equal("Corpus recommended action for CHK-100", result.RecommendedAction);
            Assert.Equal("Corpus business impact for CHK-100", result.BusinessImpact);
            Assert.Equal("Corpus ELI5 description", result.Eli5Description);
            Assert.Equal("Corpus ELI5 remediation", result.Eli5Remediation);
            Assert.Equal(2.5, result.EffortHours);
            Assert.Equal(3, result.ScoreWeight);
        }

        [Fact]
        public void ImportFile_StampsImportProvenance_OnEveryLandedRow()
        {
            // #84: imported rows must land carrying provenance (ImportedAtUtc + source file) so the
            // /audit grid can badge them "Imported" and never let a reader mistake an imported
            // result for a locally-executed one. The truthful Pass/Fail verdict stays untouched.
            var server = UniqueServer("provenance");
            TrackForCleanup(server);

            var before = DateTime.UtcNow.AddSeconds(-1);
            var payload = new QuickCheckResultStore.RunPayload
            {
                ServerName = server,
                SchemaVersion = 1,
                Results = new List<CheckResult>
                {
                    new CheckResult { CheckId = "CHK-100", CheckName = "A", Passed = false, InstanceName = server },
                    new CheckResult { CheckId = "CHK-101", CheckName = "B", Passed = true,  InstanceName = server },
                },
            };
            var json = JsonSerializer.Serialize(payload);

            var outcome = _importer.ImportFile("capture-file.json", json);
            Assert.True(outcome.Success, outcome.Error);

            var landed = _store.ReadLatestRun(server)!;
            Assert.Equal(2, landed.Count);
            foreach (var r in landed)
            {
                Assert.NotNull(r.ImportedAtUtc);
                Assert.True(r.ImportedAtUtc >= before, "import time should be stamped at import, not left null/default");
                Assert.Equal("capture-file.json", r.ImportSourceFile);
                Assert.True(ImportProvenance.IsImported(r));
            }
            // The whole run reads as imported and carries a single coherent import time.
            Assert.True(ImportProvenance.RunIsImported(landed));
            Assert.NotNull(ImportProvenance.RunImportTime(landed));
            // Verdicts are untouched by stamping — enrichment/provenance never rewrites Passed.
            Assert.False(landed.Single(r => r.CheckId == "CHK-100").Passed);
            Assert.True(landed.Single(r => r.CheckId == "CHK-101").Passed);
        }

        [Fact]
        public void ImportFile_NeverOverwritesNonEmptyFileValues()
        {
            var server = UniqueServer("noclobber");
            TrackForCleanup(server);

            var payload = new QuickCheckResultStore.RunPayload
            {
                ServerName = server,
                SchemaVersion = 1,
                Results = new List<CheckResult>
                {
                    new CheckResult
                    {
                        CheckId = "CHK-100",
                        CheckName = "Page Life Expectancy",
                        Category = "Memory",
                        Severity = "Warning",
                        Passed = false,
                        // File already carries its own Description and EffortHours — must survive.
                        Description = "File-provided description, not corpus's",
                        EffortHours = 9.5,
                        RecommendedAction = null,
                        ScoreWeight = 0,
                    },
                },
            };
            var json = JsonSerializer.Serialize(payload);

            var outcome = _importer.ImportFile("noclobber.json", json);

            Assert.True(outcome.Success);
            // RecommendedAction/ScoreWeight (and the untouched BusinessImpact/Eli5* fields) were
            // still empty/default, so this result counts as enriched (>=1 field filled) — but
            // Description/EffortHours, which the file already supplied, are left alone below.
            Assert.Equal(1, outcome.EnrichedCount);

            var landed = _store.ReadLatestRun(server)!;
            var result = Assert.Single(landed);
            Assert.Equal("File-provided description, not corpus's", result.Description);
            Assert.Equal(9.5, result.EffortHours);
            Assert.Equal("Corpus recommended action for CHK-100", result.RecommendedAction);
            Assert.Equal(3, result.ScoreWeight);
        }

        [Fact]
        public void ImportFile_SecondImportForSameServer_Wins_AndNeverMerges()
        {
            // #84 precedence at the persistence layer that feeds the /audit grid: a later imported
            // run for the same server WINS (newest run) and the store serves it back WHOLE — the
            // earlier run's rows are never merged in (that would double-count). ReadLatestRun is
            // exactly what CheckExecutionService.GetResults reads, so the grid inherits this rule.
            var server = UniqueServer("newestwins");
            TrackForCleanup(server);

            var first = JsonSerializer.Serialize(new QuickCheckResultStore.RunPayload
            {
                ServerName = server,
                SchemaVersion = 1,
                Results = new List<CheckResult>
                {
                    new CheckResult { CheckId = "CHK-OLD-1" },
                    new CheckResult { CheckId = "CHK-OLD-2" },
                },
            });
            var second = JsonSerializer.Serialize(new QuickCheckResultStore.RunPayload
            {
                ServerName = server,
                SchemaVersion = 1,
                Results = new List<CheckResult> { new CheckResult { CheckId = "CHK-NEW-1" } },
            });

            Assert.True(_importer.ImportFile("first.json", first).Success);
            Assert.True(_importer.ImportFile("second.json", second).Success);

            var landed = _store.ReadLatestRun(server)!;
            // Exactly the second run — one row, not three; the first run's ids are absent (no merge).
            var single = Assert.Single(landed);
            Assert.Equal("CHK-NEW-1", single.CheckId);
            Assert.Equal("second.json", single.ImportSourceFile);
        }

        [Fact]
        public void ImportFile_UnknownCheckId_CountsAsUnenriched_ButStillLands()
        {
            var server = UniqueServer("unmatched");
            TrackForCleanup(server);

            var payload = new QuickCheckResultStore.RunPayload
            {
                ServerName = server,
                SchemaVersion = 1,
                Results = new List<CheckResult>
                {
                    new CheckResult
                    {
                        CheckId = "CHK-DOES-NOT-EXIST",
                        CheckName = "Some Retired Check",
                        Category = "Custom",
                        Severity = "Warning",
                        Passed = true,
                        Description = null,
                    },
                },
            };
            var json = JsonSerializer.Serialize(payload);

            var outcome = _importer.ImportFile("unmatched.json", json);

            Assert.True(outcome.Success);
            Assert.Equal(0, outcome.EnrichedCount);
            Assert.Equal(1, outcome.UnenrichedCount);

            var landed = _store.ReadLatestRun(server)!;
            Assert.Single(landed);
        }

        [Fact]
        public void ImportFile_MalformedJson_ReturnsClearError_NeverThrows()
        {
            var outcome = _importer.ImportFile("bad.json", "{ this is not valid json ]");

            Assert.False(outcome.Success);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
        }

        [Fact]
        public void ImportFile_MissingServerName_Rejected()
        {
            var json = JsonSerializer.Serialize(new QuickCheckResultStore.RunPayload
            {
                ServerName = "",
                SchemaVersion = 1,
                Results = new List<CheckResult> { new CheckResult { CheckId = "CHK-100" } },
            });

            var outcome = _importer.ImportFile("noserver.json", json);

            Assert.False(outcome.Success);
            Assert.Contains("ServerName", outcome.Error);
        }

        [Fact]
        public void ImportFile_EmptyResults_Rejected()
        {
            var json = JsonSerializer.Serialize(new QuickCheckResultStore.RunPayload
            {
                ServerName = "SOMESERVER",
                SchemaVersion = 1,
                Results = new List<CheckResult>(),
            });

            var outcome = _importer.ImportFile("noresults.json", json);

            Assert.False(outcome.Success);
        }

        [Fact]
        public void ImportFile_SchemaVersionMismatch_WarnsButStillImports()
        {
            var server = UniqueServer("schemamismatch");
            TrackForCleanup(server);

            var json = JsonSerializer.Serialize(new QuickCheckResultStore.RunPayload
            {
                ServerName = server,
                SchemaVersion = 2,
                Results = new List<CheckResult> { new CheckResult { CheckId = "CHK-100" } },
            });

            var outcome = _importer.ImportFile("schema2.json", json);

            Assert.True(outcome.Success);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Warning));
        }

        [Fact]
        public void ImportPaths_MultiFile_AggregatesAcrossServers()
        {
            var serverA = UniqueServer("batchA");
            var serverB = UniqueServer("batchB");
            TrackForCleanup(serverA);
            TrackForCleanup(serverB);

            var dir = Path.Combine(Path.GetTempPath(), $"sqltriage-import-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "a.json"), JsonSerializer.Serialize(new QuickCheckResultStore.RunPayload
                {
                    ServerName = serverA,
                    SchemaVersion = 1,
                    Results = new List<CheckResult> { new CheckResult { CheckId = "CHK-100" } },
                }));
                File.WriteAllText(Path.Combine(dir, "b.json"), JsonSerializer.Serialize(new QuickCheckResultStore.RunPayload
                {
                    ServerName = serverB,
                    SchemaVersion = 1,
                    Results = new List<CheckResult> { new CheckResult { CheckId = "CHK-100" }, new CheckResult { CheckId = "CHK-100" } },
                }));

                var batch = _importer.ImportPaths(new[] { dir });

                Assert.Equal(2, batch.Files.Count);
                Assert.Equal(2, batch.SucceededCount);
                Assert.Equal(3, batch.TotalResultsImported); // 1 from a.json + 2 from b.json
                Assert.Empty(batch.PathsWithNoFiles);        // a populated folder yields no empty-path
                Assert.NotNull(_store.ReadLatestRun(serverA));
                Assert.NotNull(_store.ReadLatestRun(serverB));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void ImportPaths_EmptyDirectory_IsReportedNotSilent()
        {
            // platform-r2-04: --import against an empty folder printed "complete. 0/0 file(s)
            // imported" and exited 0 — nothing said the path yielded zero files. ImportPaths now
            // records the path so the CLI can name it and exit non-zero.
            var dir = Path.Combine(Path.GetTempPath(), $"sqltriage-import-empty-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var batch = _importer.ImportPaths(new[] { dir });

                Assert.Empty(batch.Files);                    // nothing imported
                Assert.Equal(0, batch.FailedCount);           // and it is NOT a per-file failure…
                Assert.Equal(new[] { dir }, batch.PathsWithNoFiles); // …it is a path that yielded nothing
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
