/* In the name of God, the Merciful, the Compassionate */

// Honesty pins for the Executive Summary bundle (lane reports, 2026-08-27): reports-r1-01 (the
// fabricated estate score, #7 in the wave top-ten) and reports-r2-02 (a check that could not RUN
// rendered as an ordinary finding).
//
// This file lives in Gated/ because it binds category-2 symbols: both
// ReportBundleService.BuildExecutiveSummaryEstatePdfAsync and AssessmentPdf.BuildExecutiveSummaryBundle
// sit behind #if !SQLT_NO_REPORT_EXEC_SUMMARY, and buildprofile.json has reports."executive-summary"
// = "off", so neither exists in the community assembly (CS0117 — the files themselves still
// compile). See Gated/README.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests.Gated
{
    public class ExecutiveSummaryEstateHonestyTests : IDisposable
    {
        static ExecutiveSummaryEstateHonestyTests()
        {
            // App.xaml.cs sets this at startup; tests bypass startup, so set it here.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        private readonly string _tempDir;

        public ExecutiveSummaryEstateHonestyTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "execsummary-estate-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        /// <summary>Every dependency over temp files — same construction as ReportBundleServiceTests,
        /// which cannot be reused directly because that fixture is not profile-gated and this one
        /// must be.</summary>
        private ReportBundleService NewService(CheckExecutionService? checkExecution = null)
        {
            var dbPath = Path.Combine(_tempDir, "governance-history.db");
            var vaState = new VulnerabilityAssessmentStateService();
            var userSettings = new UserSettingsService(Path.Combine(_tempDir, "user-settings.json"));
            var blockingHistory = new BlockingHistoryService(
                NullLogger<BlockingHistoryService>.Instance, retentionDays: 30,
                dbPath: Path.Combine(_tempDir, "blocking-history.db"));
            var perfHistory = new HistoricalPerformanceService(
                NullLogger<HistoricalPerformanceService>.Instance, dbPath: dbPath);
            var govHistory = new GovernanceHistoryService(
                NullLogger<GovernanceHistoryService>.Instance, retentionDays: 365, dbDir: _tempDir);
            var healthCheckSvc = new HealthCheckService(new NullDbConnectionFactory());
            var executiveHealth = new ExecutiveHealthService(
                healthCheckSvc, govHistory, blockingHistory, perfHistory, vaState,
                NullLogger<ExecutiveHealthService>.Instance);
            var checkRepo = new CheckRepositoryService(NullLogger<CheckRepositoryService>.Instance);

            return new ReportBundleService(
                executiveHealth, healthCheckSvc, vaState, userSettings, checkRepo,
                new OwnerAssignmentStore(), NullLogger<ReportBundleService>.Instance,
                auditLog: null, checkExecution: checkExecution);
        }

        // ── An executor with real rows and no disk ───────────────────────────────────────────
        //
        // The estate denominator comes from CheckExecutionService, so pinning it needs one. The
        // result store is NULL on purpose: its root is fixed under the test binary and
        // GetServersWithResults() spans every server any fixture has left a run file for, so a store
        // here would read other fixtures' residue and an exact-count assertion would be a flake.
        // Same reason, same shape as Gated/ReportBundleLiveSmokeTests. The rows stay in the hot cache
        // for this process only, and the servers are named so nothing else can collide with them.

        /// <summary>A check that produces a result without reaching any server: host-probe with no
        /// HostProbeService wired is the documented SKIP-with-reason path (CheckExecutionService),
        /// which sets Passed=true — i.e. a CLEAN server, which is exactly the shape reports-r2-01 is
        /// about. Borrowed from LastRunResultsIsolationTests, which uses it for the same reason.</summary>
        private static SqlCheck OfflineCheck(string id, string category) => new()
        {
            Id = id, Name = id, Category = category, Method = "host-probe", ProbeKey = "host.none",
        };

        /// <summary>A check that CANNOT RUN, produced by the real executor instead of asserted into
        /// existence. <c>ExecuteSingleCheckAsync</c> returns "Check has no SQL query defined" before
        /// it opens any connection, so the result carries an ErrorMessage with Passed=false — which
        /// is what <c>CheckClassification.IsSkip</c> reads, so
        /// <c>ComplianceScoreService.StatusFor</c> tags it Unassessed. Same shape as a check whose
        /// SQL errors against a live instance (the gate's probe used an "Invalid object name"), and
        /// it needs no instance to reproduce.</summary>
        private static SqlCheck UnrunnableCheck(string id, string category) => new()
        {
            Id = id, Name = id, Category = category, Method = "tsql", Severity = "High",
            // SqlQuery deliberately unset. That is the whole fixture.
        };

        private static ServerConnection OfflineConnection() => new()
        {
            Id = Guid.NewGuid().ToString(),
            ServerNames = "tcp:localhost,56599",   // nothing listens here; nothing dials it either
            UseWindowsAuthentication = true,
            ConnectionTimeout = 2,
            IsEnabled = true,
        };

        private CheckExecutionService NewExecutor(ISeatRegister? seats = null, SqlCheck? extraCheck = null)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    // Leaves no audit-diag-*.jsonl residue under the test binary. Same
                    // process-wide static ReportBundleLiveSmokeTests documents.
                    ["CheckExecution:DiagnosticJsonl"] = "false",
                }).Build();

            var connMgr = new ServerConnectionManager(
                NullLogger<ServerConnectionManager>.Instance, seats: null,
                connectionsFilePath: Path.Combine(_tempDir, "connections.json"));
            var checkRepo = new CheckRepositoryService(
                NullLogger<CheckRepositoryService>.Instance, configuration);
            checkRepo.AddCheck(OfflineCheck("ZZ-CFG-1", "Configuration"));
            if (extraCheck != null) checkRepo.AddCheck(extraCheck);

            return new CheckExecutionService(
                NullLogger<CheckExecutionService>.Instance, checkRepo, connMgr, configuration,
                resultStore: null, seats: seats);
        }

        /// <summary>Runs the offline catalogue against <paramref name="servers"/> so each one has
        /// results and none has a finding: the wholly-clean estate this defect made invisible.</summary>
        private static async Task SeedCleanServersAsync(CheckExecutionService executor, params string[] servers)
        {
            foreach (var s in servers)
            {
                var summary = await executor.ExecuteChecksAsync(OfflineConnection(), s);
                Assert.Equal(1, summary.TotalChecks);   // the run really executed
                Assert.DoesNotContain(executor.GetResults(s, 50), r => !r.Passed);
            }
        }

        // ── reports-r1-01 (#7 in the wave top-ten): the estate cover fabricated a 0/100 score ─────
        //
        // BuildExecutiveSummaryEstatePdfAsync set Score = 0 and left ScoreAssessed at its `true`
        // default, and the builder's estate test was "any top risk with ServersTotal > 0" — false for
        // an estate with no findings. So an estate nothing had measured fell through to the
        // single-server donut branch and printed a red "0 / 100 Overall Health Score" on the cover of
        // a client deliverable. Quoted from the render that found it, over two seated and entirely
        // clean servers: "Executive Summary — Estate All Servers (0) ... 0 / 100 Overall Health Score
        // Estate roll-up across 0 servers."
        //
        // No CheckExecutionService is wired here, which is the same shape for this path: an empty
        // roll-up over an estate with nothing to report.
        [Fact]
        public async Task EstateExecutiveSummaryPdf_StatesTheAbsenceOfAScore_NeverPrintsAZeroDonut()
        {
            var text = ReportBundleServiceTests.PdfText(
                await NewService().BuildExecutiveSummaryEstatePdfAsync(watermark: false));

            // The extractor must actually be reading this document, or every DoesNotContain below
            // passes vacuously on an empty string. (PdfText strips whitespace — Skia writes kerned
            // glyph runs, so the words come back split.)
            Assert.Contains("EstateOverview", text);

            // The fabrication: the donut prints its value over its "/ 100" sublabel.
            Assert.DoesNotContain("/100", text);

            // …and the absence is stated in words, not left as a missing ring the reader is free to
            // take for a perfect one.
            Assert.Contains("Nooverallhealthscoreisshown", text);
            Assert.Contains("0serversscanned", text);
        }

        [Fact]
        public void TheZeroDonutAssertionDiscriminates_ProvedByRenderingThePreFixDto()
        {
            // Mutation control for the test above. Its "/100" assertion is only worth something if
            // the SAME builder does print that string when handed what this path used to hand it:
            // Score = 0, ScoreAssessed left at its `true` default, no estate flag, and an empty
            // roll-up (so the old "any risk with ServersTotal > 0" estate test was false). Without
            // this, a builder that silently stopped drawing donuts would pass the test above.
            var preFix = new ExecutiveSummaryBundle
            {
                Meta = new AssessmentMeta { Title = "Executive Summary — Estate", GeneratedUtc = "2026-08-27T00:00Z", RunId = "prefix01" },
                Score = 0,
                ScoreMessage = "Estate roll-up across 0 servers.",
                TopRisks = new List<BundleFinding>(),
            };

            var text = ReportBundleServiceTests.PdfText(AssessmentPdf.BuildExecutiveSummaryBundle(preFix));

            Assert.Contains("0/100", text);
            Assert.DoesNotContain("Nooverallhealthscoreisshown", text);
        }

        // ── reports-r2-01, at the call site: the estate count is the SCANNED set ──────────────────
        //
        // GatherEstateRollup derived its "N servers scanned" denominator from GetMergedFindings,
        // which keeps only failing rows, so a scanned-and-clean server was invisible and a wholly
        // clean estate reported "0 servers scanned" on the cover of a client deliverable. The fix is
        // one line — EstateServerScope(ScannedEstateServers(), raw) — and nothing exercised it: the
        // EstateServerScope_* tests call the extracted helper directly, and the estate render test
        // above builds its service with no CheckExecutionService, so it reads "0 servers scanned"
        // either way. Reverting that line to the finding-derived list therefore shipped a fully
        // green suite. This test is the missing arm: real executor, real rows, real service, real
        // PDF, and the denominator read off the delivered bytes.
        [Fact]
        public async Task EstateExecutiveSummaryPdf_CountsEveryScannedServer_NotOnlyTheOnesWithFindings()
        {
            using var executor = NewExecutor();
            await SeedCleanServersAsync(executor, "ZZESTATE1", "ZZESTATE2");

            var text = ReportBundleServiceTests.PdfText(
                await NewService(executor).BuildExecutiveSummaryEstatePdfAsync(watermark: false));

            Assert.Contains("EstateOverview", text);       // the extractor is reading the document
            Assert.Contains("AllServers(2)", text);        // …the cover names the scanned estate…
            Assert.Contains("2serversscanned", text);      // …and so does the scope line.

            // The pre-fix reading of the same estate, quoted from the render that found it:
            // "Executive Summary — Estate All Servers (0) ... Estate roll-up across 0 servers."
            Assert.DoesNotContain("AllServers(0)", text);
            Assert.DoesNotContain("0serversscanned", text);
        }

        // ── The seat filter's other half: what the count leaves out ──────────────────────────────
        //
        // GetServersWithResults' own contract (CheckExecutionService) says "Callers that list servers
        // must surface GetExcludedServers() so the omission is stated, never silent … If you add a
        // caller, wire the banner too." The estate roll-up became such a caller in this lane and did
        // not wire it, so a licence-excluded server with real results vanished from a client-facing
        // "N servers scanned" claim with nothing saying so. SeatFilter.ExclusionNotice is the repo's
        // own banner wording and it now rides the bundle title band.
        [Fact]
        public async Task EstateExecutiveSummaryPdf_NamesTheServersTheLicenceExcluded_BesideTheCount()
        {
            using var executor = NewExecutor(seats: new OneSeatRegister("ZZSEATED"));
            await SeedCleanServersAsync(executor, "ZZSEATED");

            // A second server with results that the licence does not cover. Seeded through the same
            // real run, so the exclusion is the ONLY reason it is not in the count.
            var summary = await executor.ExecuteChecksAsync(OfflineConnection(), "ZZUNSEATED");
            Assert.Equal(1, summary.TotalChecks);

            var text = ReportBundleServiceTests.PdfText(
                await NewService(executor).BuildExecutiveSummaryEstatePdfAsync(watermark: false));

            Assert.Contains("EstateOverview", text);
            Assert.Contains("1serverscanned", text);       // the count is the SEATED set…
            Assert.Contains("1instanceexcluded", text);    // …and the cover says what it excludes…
            Assert.Contains("notcoveredbyyourlicence", text.ToLowerInvariant());
            Assert.Contains("ZZUNSEATED", text);           // …by name.
        }

        /// <summary>A register that seats exactly one instance. Only the two members the report path
        /// touches are implemented; the rest throw rather than returning a plausible default, so a
        /// future caller reaching for them fails loudly here instead of being quietly answered.</summary>
        private sealed class OneSeatRegister : ISeatRegister
        {
            private readonly string _seated;
            public OneSeatRegister(string seated) => _seated = seated;

            public bool IsSeated(string instanceName) =>
                string.Equals(instanceName, _seated, StringComparison.OrdinalIgnoreCase);

            public SeatFilter Filter(IEnumerable<string> instanceNames)
            {
                var all = instanceNames.ToList();
                return new SeatFilter(
                    all.Where(IsSeated).ToList(),
                    all.Where(n => !IsSeated(n)).ToList());
            }

            public SeatSummary Status() => throw new NotSupportedException();
            public SeatDecision ClaimOnProbe(InstanceFingerprint fingerprint, string instanceName) => throw new NotSupportedException();
            public SeatDecision Release(string fingerprint) => throw new NotSupportedException();
            public SeatDecision ReleaseInstance(string instanceName) => throw new NotSupportedException();
            public SeatDecision CanAdmit(int resultingInstanceCount) => throw new NotSupportedException();
            public bool IsLocked => false;
            public string? VerifyChain() => null;
            public event Action? SeatsChanged { add { } remove { } }
        }

        // ── reports-r2-02: a check that could not RUN rendered as an ordinary finding ─────────────
        //
        // GetMergedFindings tags it Status="Unassessed" and nothing downstream read the field: the
        // client-facing tables carried no Status column at all, so an errored check and a genuine
        // failure were the same row. Measured before the fix, the top row of a client-facing
        // "Top 5 Risks": "E1 Critical TDE enabled on all databases Security Error: Login failed for
        // user 'x'." The Risk Register built from the SAME result set filtered those rows out — two
        // deliverables from one dataset disagreeing about what a finding is.
        [Fact]
        public void ExecutiveSummaryPdf_MarksACheckThatCouldNotRun_InsteadOfRenderingItAsAFinding()
        {
            var dto = new ExecutiveSummaryBundle
            {
                Meta = new AssessmentMeta { Title = "Executive Summary", GeneratedUtc = "2026-08-27T00:00Z", RunId = "r2020001" },
                ScoreAssessed = true,
                Score = 60,
                TopRisks = new List<BundleFinding>
                {
                    new() { Id = "E1", Severity = "Critical", Name = "TDE enabled on all databases",
                            Category = "Security", Message = "Error: Login failed for user 'x'.",
                            Status = ComplianceScoreService.UnassessedStatus },
                    new() { Id = "F1", Severity = "High", Name = "Backups current",
                            Category = "Reliability", Message = "Last full backup > 7 days", Status = "Failed" },
                },
            };

            var text = ReportBundleServiceTests.PdfText(AssessmentPdf.BuildExecutiveSummaryBundle(dto));

            Assert.Contains("Top5Risks", text);              // the extractor is reading the document
            Assert.Contains("Status", text);                 // the column exists…
            Assert.Contains("Couldnotrun", text);            // …and says what happened to E1
            Assert.Contains("Failed", text);                 // …while a real finding still reads as one
        }

        // ── reports-r2-02, second round: "Could not run" and "2 of 2" in the SAME row ─────────────
        //
        // The first round rendered the Status the service had already computed. It left the cell
        // beside it printing the impacted-server count, which GatherEstateRollup derived from every
        // row in the group with no Status filter at all. So the cover page of a client deliverable
        // stated, in one row, that a check could not run AND that it impacted N of N servers — and
        // the DBA Handoff's own "Appendix — Failed Checks by Server" listed neither, contradicting
        // its own findings table inside one document. Quoted from the gate's live probe against
        // .\OLD2017: "SQLT-ZZGATE-BAD Critical Could not run Gate probe: check whose SQL cannot run
        // Security 1 of 1".
        //
        // Real executor, real rows, real service, real PDF bytes: the count is read off the
        // delivered artefact, not off a DTO this test filled in itself.
        [Fact]
        public async Task EstateBundles_PrintNoImpactCount_ForACheckThatCouldNotRunOnAnyServer()
        {
            const string Unrunnable = "ZZ-NOSQL-1";

            using var executor = NewExecutor(extraCheck: UnrunnableCheck(Unrunnable, "Security"));
            foreach (var server in new[] { "ZZIMPACT1", "ZZIMPACT2" })
            {
                var summary = await executor.ExecuteChecksAsync(OfflineConnection(), server);
                Assert.Equal(2, summary.TotalChecks);   // the run really executed both checks
                Assert.Equal(1, summary.Errors);        // …and one of them could not run
                Assert.Contains(executor.GetResults(server, 50),
                    r => r.CheckId == Unrunnable && !r.Passed && r.ErrorMessage != null);
            }

            var service = NewService(executor);

            // Asserted on the ROW, not on the document: "of 2" anywhere in that row is a count, and
            // the assertion has to fail on ALL of them. The first draft of this test asserted
            // DoesNotContain("2of2") and passed against a deliberately reverted renderer, because
            // the count had by then become 0 and the row read "0 of 2" — which claims the estate was
            // measured and found clean, the same fabrication wearing a smaller number.
            foreach (var (what, bytes) in new[]
            {
                ("Executive Summary (estate)", await service.BuildExecutiveSummaryEstatePdfAsync(watermark: false)),
                ("DBA Handoff (estate)",       await service.BuildDbaHandoffEstatePdfAsync(watermark: false)),
            })
            {
                var text = ReportBundleServiceTests.PdfText(bytes);
                var at = text.IndexOf(Unrunnable, StringComparison.Ordinal);
                Assert.True(at >= 0, what + ": the did-not-run row is not in the findings table, so "
                    + "nothing below is asserting anything about how such a row renders");

                // One row wide: id + severity + status + check + category (+ source) + the impact
                // cell. The estate roll-up holds exactly one row here — the host-probe check passes,
                // so it is not a finding — but the window keeps this honest if that ever changes.
                var row = text.Substring(at, Math.Min(70, text.Length - at));
                Assert.Contains("Couldnotrun", row);
                Assert.DoesNotContain("of2", row);
            }

            // The HTML twins, built by the same service from the same rollup. The PDF and its twin
            // described one row two different ways once already (reports-r2-06); the impact cell now
            // comes from one property so they cannot.
            foreach (var html in new[]
            {
                await service.PrepareExecutiveSummaryEstateHtmlAsync(),
                await service.PrepareDbaHandoffEstateHtmlAsync(),
            })
            {
                Assert.Contains("<td>Could not run</td>", html, StringComparison.Ordinal);
                Assert.DoesNotContain(" of 2</td>", html, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void TheImpactCountAssertionDiscriminates_ProvedByRenderingAFindingThatDidRun()
        {
            // Mutation control, and the other half of the gate's probe shape (one errored check plus
            // one genuine failure). The test above is only worth something if the SAME table still
            // prints a real count for a row that measured something — otherwise a builder that had
            // simply stopped rendering the column would pass it, trading an over-claim for an
            // omission. The two rows carry different counts so each assertion names exactly one.
            var dto = new ExecutiveSummaryBundle
            {
                Meta = new AssessmentMeta { Title = "Executive Summary", GeneratedUtc = "2026-08-27T00:00Z", RunId = "r2020002" },
                EstateScope = true,
                TopRisks = new List<BundleFinding>
                {
                    new() { Id = "E1", Severity = "Critical", Name = "TDE enabled on all databases",
                            Category = "Security", Status = ComplianceScoreService.UnassessedStatus,
                            ServersImpacted = 1, ServersTotal = 2, ServersImpactedAssessed = false },
                    new() { Id = "F1", Severity = "High", Name = "Backups current",
                            Category = "Reliability", Status = "Failed",
                            ServersImpacted = 2, ServersTotal = 2, ServersImpactedAssessed = true },
                },
            };

            var text = ReportBundleServiceTests.PdfText(AssessmentPdf.BuildExecutiveSummaryBundle(dto));

            Assert.Contains("ServersImpacted", text);   // the column is being rendered at all
            Assert.Contains("2of2", text);              // the failing row states what it measured…
            Assert.DoesNotContain("1of2", text);        // …and the unassessed row states nothing.
        }
    }
}
