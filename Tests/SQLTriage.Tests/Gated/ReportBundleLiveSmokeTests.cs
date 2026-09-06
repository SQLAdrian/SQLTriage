/* In the name of God, the Merciful, the Compassionate */

// THE LIVE PROOF FOR THE REPORT BUNDLE BUILDERS (lane reports, 2026-08-27).
//
// WHY THIS FILE EXISTS. The audit lane's close left one question open, verbatim: "whether other
// QuestPDF reports carry the same Row-spine crash shape (unaudited)". That crash was a
// QuestPDF.DocumentLayoutException thrown on REAL audit data because a category card was
// unsplittable, and it was found only because that lane built
// Gated/RoadmapArtefactLiveSmokeTests.cs — a real sp_Blitz run against a real instance, rendering
// the real client PDF from what came back. Every offline render test in this repo, including all
// eighteen reproduce passes of this lane's own hunt, feeds the bundle builders one to six
// constructed rows. A layout failure depends on the SHAPE of real data: how many findings land in
// one table, how long a real corpus message is, how many servers the estate covers. So a synthetic
// fixture cannot see it, and the operator finds it in front of the customer.
//
// WHAT IT PROVES WHEN ARMED
//   The full corpus check suite runs against a REAL instance through the REAL CheckExecutionService,
//   and every client-facing bundle ReportBundleService builds composes from those results without
//   throwing: Executive Summary (single + estate), DBA Handoff (single + estate), Audit Evidence
//   (single + the estate zip), Risk Register (single + estate), and the HTML twins of the first
//   three. Each PDF is asserted to start with the %PDF magic bytes, so a builder that silently
//   returned an empty array cannot pass.
//
//   Since the 2026-08-27 fix round it also proves one CONTENT property on real data, because the
//   gate found the defect there and nowhere else: one check that cannot run is added beside the
//   real catalogue, and the estate DBA Handoff row for it must say "Could not run" and print no
//   impacted-server count, while the real failing checks in the same table still print theirs.
//
// WHAT IT DOES NOT PROVE, said plainly.
//   1. It does not drive Pages/ReportBundles.razor. The razor's own button handlers, its watermark
//      resolution and its RecordSuccess/RecordFailure calls live inside a Blazor component nothing
//      in this project renders. This file constructs ReportBundleService directly, which is the same
//      limit RoadmapArtefactLiveSmokeTests names for its own projection.
//   2. Six of ReportBundleService's dependencies are real objects over temp files rather than
//      configured services: ExecutiveHealthService, HealthCheckService (over a null connection
//      factory), VulnerabilityAssessmentStateService, UserSettingsService, CheckRepositoryService
//      and OwnerAssignmentStore. None of them needs live audit data for a compose-without-crashing
//      assertion, and every one of them is the production type. CheckExecutionService — the one that
//      actually carries the audit-scale rows — is real, live, and the point of the exercise.
//   3. It asserts composition, not content, with TWO exceptions: reports-r1-03 (a fixed row that is
//      a constant, safe to read off a live render) and reports-r2-02 (asserted on the ONE row this
//      harness controls, the probe check it adds itself). Content honesty for the rest of
//      this lane's eighteen findings is pinned by the offline fixtures in ReportBundleServiceTests,
//      Gated/ExecutiveSummaryEstateHonestyTests and Gated/ReportAttestationHonestyTests, where an
//      exact assertion is deterministic. A live instance's finding set changes between runs, so an
//      exact-count assertion here would be a flake, not a proof.
//   4. The Compliance Scorecard PDF is NOT here. ComplianceScoreService needs
//      ComplianceMappingService, which needs an IBundleAccessor — a minted licence bundle. This
//      harness deliberately mints nothing, so that builder's layout stays offline-only, in
//      Gated/ReportAttestationHonestyTests. Named rather than quietly omitted: it is the one bundle
//      the audit lane's Row-spine question is still open for.
//
// WHAT IT HANDS OVER. Every PDF, its extracted text, the three HTML twins, the estate zip — and
// MANIFEST.tsv, which lists each artefact's byte size and SHA-256 read back FROM THE FILE in the
// run that wrote it. PDFs embed a generation timestamp, so sizes move between runs of this harness;
// a size table quoted from a transcript describes a set of files that no longer exists. Quote the
// manifest, or re-run and quote the new one.
//
// INERT unless armed. LiveFactAttribute reports SKIPPED when FRK_LIVE_TARGET is unset, and
// RequireTarget fails the body rather than passing vacuously if that attribute is ever weakened.
//
// INVOCATION (first armed run 2026-08-27 against .\OLD2017):
//   $env:FRK_LIVE_TARGET = ".\OLD2017"
//   $env:FRK_LIVE_EVIDENCE_DIR = "C:\temp\reports-lane6-artefacts"
//   dotnet test SQLTriage.sln -c Debug --no-build --filter "FullyQualifiedName~ReportBundleLiveSmokeTests"
//
// FIXTURE CONTRACT. FRK_LIVE_TARGET must name an instance the caller is happy to have the corpus
// check suite READ. The suite is read-only diagnostics; unlike the roadmap smoke this file installs
// nothing and drops nothing. The catalogue is loaded through the source-parser seam from
// SQLTRIAGE_CORPUS_CHECKS (default: the path Config/appsettings.Development.json already carries),
// so no licence bundle is minted or touched. The result store is deliberately NULL and the hot-cache
// cap is raised instead, so this run's rows live only in memory and no other fixture's
// output/quickcheck files are read or written.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests.Gated
{
    public class ReportBundleLiveSmokeTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _tempDir;

        public ReportBundleLiveSmokeTests(ITestOutputHelper output)
        {
            _out = output;
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
            _tempDir = Path.Combine(Path.GetTempPath(), "reportbundle-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private void Line(string s) => _out.WriteLine(s);

        /// <summary>The one synthetic check in an otherwise real run: it cannot run, so the estate
        /// deliverables have to say so instead of counting it (reports-r2-02).</summary>
        private const string UnrunnableId = "ZZ-GATE-NOSQL";

        private static string? Target => Environment.GetEnvironmentVariable("FRK_LIVE_TARGET");
        private static string? EvidenceDir => Environment.GetEnvironmentVariable("FRK_LIVE_EVIDENCE_DIR");

        private static string RequireTarget()
        {
            Assert.False(string.IsNullOrWhiteSpace(Target),
                "FRK_LIVE_TARGET is not set, so this test has no instance to audit and nothing to "
                + "assert. It should have been SKIPPED by LiveFactAttribute; if it ran, that attribute "
                + "is no longer doing its job.");
            return Target!;
        }

        /// <summary>The corpus check sources, through the same source-parser seam dev builds use.</summary>
        private static string CorpusChecksPath()
            => Environment.GetEnvironmentVariable("SQLTRIAGE_CORPUS_CHECKS")
               ?? @"C:/GitHub/sqltriage-corpus/corpus-v2/checks";

        private static string EvidencePath(string fileName)
        {
            var dir = EvidenceDir;
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "report-bundles");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        private static void AssertIsPdf(string what, byte[] bytes)
        {
            Assert.True(bytes.Length > 0, what + " rendered zero bytes");
            Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, bytes.Take(4).ToArray());
        }

        /// <summary>
        /// Every artefact this run handed over, measured FROM THE FILE after writing it, with a
        /// SHA-256 so a quoted figure is tied to bytes a reader can re-hash.
        ///
        /// <para>WHY (2026-08-27 fix round). A PDF embeds its generation timestamp, so byte sizes
        /// move between runs of this harness — three armed runs of the same builders produced three
        /// different tables. A report that quotes run A's sizes and hands over run B's files is not
        /// fabricating anything and is still describing the wrong artefacts, which is the provenance
        /// class this wave keeps finding. The manifest is written BESIDE the files, in the same run
        /// that wrote them, so any size table can be quoted from it instead of from a transcript.</para>
        /// </summary>
        private readonly List<string> _handedOver = new();

        private void Handover(string name, string path)
        {
            var info = new FileInfo(path);
            using var stream = File.OpenRead(path);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            _handedOver.Add($"{name}\t{info.Length}\t{sha}\t{info.Name}");
            Line((name + " ").PadRight(26) + info.Length.ToString("N0").PadLeft(10) + " bytes  " + sha[..12]);
        }

        private void WriteManifest(string target, DateTime startedUtc)
        {
            var path = EvidencePath("MANIFEST.tsv");
            var lines = new List<string>
            {
                "# SQLTriage report-bundle live smoke — every artefact this run handed over.",
                "# Sizes are read from the written file, not from the in-memory array. A PDF embeds",
                "# its generation timestamp, so these move run to run: quote THIS file, or re-run.",
                "# target\t" + target,
                "# runStartedUtc\t" + startedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                "# writtenUtc\t" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                "artefact\tbytes\tsha256\tfile",
            };
            lines.AddRange(_handedOver);
            File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
            Line("manifest      : " + path + "  (" + _handedOver.Count + " artefacts)");
        }

        [LiveFact("FRK_LIVE_TARGET")]
        public async Task Every_client_bundle_composes_from_a_real_audit()
        {
            var target = RequireTarget();
            var startedUtc = DateTime.UtcNow;
            var corpusDir = CorpusChecksPath();
            Line("target        : " + target);
            Line("corpus        : " + corpusDir);
            Assert.True(Directory.Exists(corpusDir),
                "the corpus check sources are not at " + corpusDir + ". Set SQLTRIAGE_CORPUS_CHECKS "
                + "to the corpus checks directory, or this run would audit an EMPTY catalogue and "
                + "every assertion below would pass over no data at all");

            // ── The real executor, over the real corpus, against the real instance ─────────────
            //
            // resultStore is null on purpose: the store's root is fixed under the test binary and
            // GetServersWithResults() spans every server any fixture has left a run file for, so a
            // store here would both read other fixtures' residue and leave residue of its own. The
            // hot-cache cap is raised instead, so the whole run stays in this process.
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CheckRepository:UseSourceParser"] = "true",
                ["CheckRepository:SourceParserPath"] = corpusDir,
                ["CheckExecution:MaxResultsPerInstance"] = "2000",
                // ⚠ This one flips a PROCESS-WIDE static: CheckExecutionService's constructor
                // assigns AuditDiagnosticSink.Enabled from it, so the last service constructed in a
                // run wins. Set false so an armed run leaves no audit-diag-*.jsonl residue under the
                // test binary. Checked before setting it: the sink's only consumer in this suite is
                // WarnRawPassedSweepTests.AuditDiagnosticSink_RecordsTheVerdict, which reads the
                // SOURCE text and asserts nothing at runtime. Every other fixture constructing a
                // CheckExecutionService already stomps the same static with the default.
                ["CheckExecution:DiagnosticJsonl"] = "false",
            }).Build();

            var connMgr = new ServerConnectionManager(
                NullLogger<ServerConnectionManager>.Instance, seats: null,
                connectionsFilePath: Path.Combine(_tempDir, "connections.json"));
            var checkRepo = new CheckRepositoryService(
                NullLogger<CheckRepositoryService>.Instance, configuration);

            // ── One check that CANNOT RUN, beside the whole real catalogue ────────────────────
            //
            // The gate's probe shape for reports-r2-02: an errored check and a genuine failure in
            // one estate, so the deliverable has to tell them apart on real data. A check with no
            // SQL returns "Check has no SQL query defined" before any connection is opened, so this
            // adds a real did-not-run row and sends nothing to the instance.
            //
            // The order matters: EnsureCatalogueLoadedAsync only loads the catalogue when the repo
            // is EMPTY at run start, so adding this first would have replaced 582 real checks with
            // one synthetic one and every assertion below would have run over nothing.
            await checkRepo.LoadChecksAsync();
            Assert.True(checkRepo.GetEnabledChecks().Count > 100,
                "the corpus catalogue did not load, so the probe check below would be the whole run");
            checkRepo.AddCheck(new SqlCheck
            {
                Id = UnrunnableId,
                Name = "ZZ gate probe: a check with no runnable SQL",
                Category = "Security",
                Method = "tsql",
                Severity = "High",
                // SqlQuery deliberately unset. That is the whole fixture.
            });

            using var executor = new CheckExecutionService(
                NullLogger<CheckExecutionService>.Instance, checkRepo, connMgr, configuration);

            var connection = new ServerConnection
            {
                ServerNames = target,
                Database = "master",
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
            };

            var summary = await executor.ExecuteChecksAsync(connection, target);
            Line("checks run    : " + summary.TotalChecks);
            Assert.True(summary.TotalChecks > 100,
                "the catalogue collapsed to " + summary.TotalChecks + " checks, so nothing below is "
                + "rendering audit-scale data");

            var results = executor.GetResults(target, 2000);
            var failing = results.Count(r => !r.Passed);
            Line("results       : " + results.Count + "   failing: " + failing);
            Assert.True(results.Count > 100, "the executor returned " + results.Count + " results");
            Assert.True(failing > 0,
                "this instance produced no failing rows at all, so every findings table below would "
                + "render its empty state and the layout under test would never be exercised");

            var servers = executor.GetServersWithResults();
            Line("estate scope  : " + string.Join(", ", servers));
            Assert.NotEmpty(servers);

            // ── The real bundle service over those results ────────────────────────────────────
            //
            // Audit Evidence is the one bundle that does NOT read the corpus. The 2026-07-16 honesty
            // ruling carved it out as a single-source Microsoft VA attestation, so it reads
            // VulnerabilityAssessmentStateService and would render its empty state against the audit
            // above. This harness does not run a Microsoft VA against the target — so the VA state is
            // seeded from the real audit's own rows, at their real count and their real prose
            // lengths. STATED PLAINLY, because it is the one thing here that is not end-to-end: what
            // that proves is the Audit Evidence RENDER at real scale, not the VA collection. Its
            // finding-selection and hashing code is real and runs on this data.
            var vaState = new VulnerabilityAssessmentStateService();
            vaState.Results = results.Where(r => !r.Passed).Select(r => new AssessmentResult
            {
                ThisServer = target,
                CheckId = r.CheckId,
                DisplayName = r.CheckName,
                Severity = r.Severity,
                Message = r.Message,
                Category = r.Category,
                Description = r.Description ?? string.Empty,
                Remediation = r.RecommendedAction ?? string.Empty,
                Status = "Failed",
            }).ToList();
            vaState.HasRun = true;
            Line("va seeded     : " + vaState.Results.Count + " rows from the real audit");

            var userSettings = new UserSettingsService(Path.Combine(_tempDir, "user-settings.json"));
            var blockingHistory = new BlockingHistoryService(
                NullLogger<BlockingHistoryService>.Instance, retentionDays: 30,
                dbPath: Path.Combine(_tempDir, "blocking-history.db"));
            var perfHistory = new HistoricalPerformanceService(
                NullLogger<HistoricalPerformanceService>.Instance,
                dbPath: Path.Combine(_tempDir, "governance-history.db"));
            var govHistory = new GovernanceHistoryService(
                NullLogger<GovernanceHistoryService>.Instance, retentionDays: 365, dbDir: _tempDir);
            var healthCheckSvc = new HealthCheckService(new NullDbConnectionFactory());
            var executiveHealth = new ExecutiveHealthService(
                healthCheckSvc, govHistory, blockingHistory, perfHistory, vaState,
                NullLogger<ExecutiveHealthService>.Instance);

            var bundles = new ReportBundleService(
                executiveHealth, healthCheckSvc, vaState, userSettings, checkRepo,
                new OwnerAssignmentStore(), NullLogger<ReportBundleService>.Instance,
                auditLog: null, checkExecution: executor);

            // Priority order per the lane brief: the highest-blast-radius deliverables first. A
            // DocumentLayoutException here is an uncaught throw and fails the test directly — the
            // same instrument shape RoadmapArtefactLiveSmokeTests uses, and the reason no try/catch
            // wraps any of these calls.
            var rendered = new List<(string Name, byte[] Bytes)>
            {
                ("ExecutiveSummary_single", await bundles.BuildExecutiveSummaryPdfAsync(target, watermark: false)),
                ("ExecutiveSummary_estate", await bundles.BuildExecutiveSummaryEstatePdfAsync(watermark: false)),
                ("DbaHandoff_single",       await bundles.BuildDbaHandoffPdfAsync(target, watermark: false)),
                ("DbaHandoff_estate",       await bundles.BuildDbaHandoffEstatePdfAsync(watermark: false)),
                ("AuditEvidence_single",    await bundles.BuildAuditEvidencePdfAsync(target, watermark: false)),
                ("RiskRegister_single",     await bundles.BuildRiskRegisterPdfAsync(target, acknowledgement: false, formalTone: true, preparedBy: "live probe", watermark: false)),
                ("RiskRegister_estate",     await bundles.BuildRiskRegisterPdfAsync(null, acknowledgement: true, formalTone: true, preparedBy: "live probe", watermark: false)),
            };

            foreach (var (name, bytes) in rendered)
            {
                AssertIsPdf(name, bytes);
                var path = EvidencePath(name + "_live.pdf");
                File.WriteAllBytes(path, bytes);
                // The extracted text beside the PDF, so a reviewer can read what was delivered
                // without re-running this harness or owning a PDF reader. Unstripped: the stripped
                // form the assertions use is unreadable, and this file is for a human.
                File.WriteAllText(EvidencePath(name + "_live.txt"),
                    PdfTextExtractor.Extract(bytes), new System.Text.UTF8Encoding(false));
                Handover(name, path);
            }

            // reports-r1-03, read off a REAL delivered artefact rather than a fixture. The Audit
            // Evidence PDF's Report Integrity table printed a hard-coded ("Report Period","Last 30
            // days") two rows above the SHA-256 of a findings body no date filter had ever touched.
            // Both strings are constants, so this is safe to assert on a live render.
            var auditText = ReportBundleServiceTests.PdfText(
                rendered.Single(r => r.Name == "AuditEvidence_single").Bytes);
            Assert.Contains("ReportIntegrity", auditText);
            Assert.DoesNotContain("ReportPeriod", auditText);
            Assert.Contains("Nodatefilterisappliedtothehashedfindings", auditText);

            // ── reports-r2-02, second round, on a REAL estate render ──────────────────────────
            //
            // The first round gave the findings table a Status column, and left the cell beside it
            // printing an impacted-server count computed with no Status filter. The gate read this
            // off a live render: "SQLT-ZZGATE-BAD Critical Could not run … Security 1 of 1" — a
            // check that could not run, claiming one of one servers, in a document whose own
            // "Failed Checks by Server" appendix listed only the check that genuinely failed. The
            // count is now printed only when it counts something.
            //
            // Asserted on the ROW, not the document: with a one-server estate every genuine finding
            // legitimately reads "1 of N", so a document-wide DoesNotContain would be measuring the
            // wrong thing. The window is 120 characters from the id, which is one row wide (id +
            // severity + status + name + category + source + the impact cell) and cannot reach the
            // next row's impact cell.
            var handoffEstate = ReportBundleServiceTests.PdfText(
                rendered.Single(r => r.Name == "DbaHandoff_estate").Bytes);
            var probeAt = handoffEstate.IndexOf(UnrunnableId, StringComparison.Ordinal);
            Assert.True(probeAt >= 0,
                "the did-not-run probe check is not in the estate findings table, so nothing below "
                + "is asserting anything about how such a row renders");
            var probeRow = handoffEstate.Substring(probeAt, Math.Min(120, handoffEstate.Length - probeAt));
            Line("probe row     : " + probeRow);
            Assert.Contains("Couldnotrun", probeRow);
            Assert.DoesNotContain("of" + servers.Count, probeRow);

            // …and the same table still states the count for checks that DID run, on this instance's
            // real findings. Without this the assertion above would also pass on a build that had
            // simply stopped rendering the column.
            Assert.Contains("1of" + servers.Count, handoffEstate);

            // The estate zip: every per-server PDF composes, and none is dropped. skippedServers is
            // the platform-r2-06 out-parameter — an empty list here is the honest healthy state, and
            // a non-empty one would name a server whose own PDF threw.
            //
            // Its scope is ScannedServers(), the VA-side estate, NOT the corpus estate above. The two
            // are different sets by design and the first armed run of this file asserted them equal,
            // which produced a 22-byte empty zip and a red test. Left as the load-bearing comment it
            // is: an assertion that conflates them is measuring the wrong estate.
            var attested = bundles.ScannedServers();
            Assert.NotEmpty(attested);

            var skipped = new List<string>();
            var zipBytes = await bundles.BuildAuditEvidenceEstateZipAsync(watermarkAll: false, skippedServers: skipped);
            Assert.True(zipBytes.Length > 0, "the estate audit-evidence zip rendered zero bytes");
            Assert.Empty(skipped);

            using (var ms = new MemoryStream(zipBytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                Line("estate zip    : " + zip.Entries.Count + " entries, " + zipBytes.Length.ToString("N0") + " bytes");
                Assert.Equal(attested.Count, zip.Entries.Count);
                foreach (var entry in zip.Entries)
                {
                    using var es = entry.Open();
                    using var buf = new MemoryStream();
                    es.CopyTo(buf);
                    AssertIsPdf(entry.FullName, buf.ToArray());
                }
            }
            var zipPath = EvidencePath("AuditEvidence_estate_live.zip");
            File.WriteAllBytes(zipPath, zipBytes);
            Handover("AuditEvidence_estate.zip", zipPath);

            // The HTML twins, built by the same service in the same run. They share the
            // finding-selection code with the PDFs, so a twin that threw would mean the two
            // deliverables disagree about what they can build from one dataset.
            foreach (var (name, html) in new[]
            {
                ("ExecutiveSummary", await bundles.PrepareExecutiveSummaryHtmlAsync(target)),
                ("DbaHandoff",       await bundles.PrepareDbaHandoffHtmlAsync(target)),
                ("AuditEvidence",    await bundles.PrepareAuditEvidenceHtmlAsync(target)),
            })
            {
                Assert.False(string.IsNullOrWhiteSpace(html), name + " HTML twin came back empty");
                var htmlPath = EvidencePath(name + "_live.html");
                File.WriteAllText(htmlPath, html, new System.Text.UTF8Encoding(false));
                Handover(name + "_html", htmlPath);

                // reports-r1-03 on the twin: the two deliverables must not disagree about whether a
                // reporting window applies to the hashed findings.
                if (name == "AuditEvidence")
                {
                    Assert.DoesNotContain("<th>Report Period</th>", html, System.StringComparison.Ordinal);
                    Assert.Contains("<th>Findings Scope</th>", html, System.StringComparison.Ordinal);
                }
            }

            // Last, so it describes everything: 11 artefacts, sized and hashed from disk.
            WriteManifest(target, startedUtc);
            Assert.Equal(11, _handedOver.Count);
        }
    }
}
