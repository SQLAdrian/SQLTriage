/* In the name of God, the Merciful, the Compassionate */
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Tests for ReportBundleService HTML composition.
    /// ReportBundleService depends on ExecutiveHealthService, HealthCheckService,
    /// VulnerabilityAssessmentStateService, and UserSettingsService.
    /// ExecutiveHealthService in turn pulls from GovernanceHistoryService,
    /// BlockingHistoryService, HistoricalPerformanceService, and VulnerabilityAssessmentStateService.
    /// All are constructed with empty/default state and temp directories.
    ///
    /// HTML generation is pure (no DB writes from the bundle service itself), so tests
    /// assert structure (tag/keyword presence) not exact content.
    /// </summary>
    public class ReportBundleServiceTests : IDisposable
    {
        static ReportBundleServiceTests()
        {
            // App.xaml.cs sets this at startup; tests bypass startup, so set it here (same
            // convention as RiskAssessmentPdfTests / the other PDF-builder test files).
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        private readonly string _tempDir;

        public ReportBundleServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "reportbundle-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private ReportBundleService NewService(AuditLogService? auditLog = null)
            => NewServiceWithState(auditLog).Svc;

        /// <summary>
        /// Same construction as <see cref="NewService"/>, but also returns the
        /// <see cref="VulnerabilityAssessmentStateService"/> instance the service was built with, so
        /// a test can seed it (Results/HasRun) the same way
        /// Pages/VulnerabilityAssessment.razor.cs:452,476 does after a real scan, before exercising
        /// the Audit Evidence PDF path (which reads VA state, not a corpus run).
        /// </summary>
        /// <summary>The constructor dependencies, built over temp files, shared by the plain and the
        /// throwing-subclass builders so both construct the service identically.</summary>
        private (ExecutiveHealthService Exec, HealthCheckService Health, VulnerabilityAssessmentStateService Va,
                 UserSettingsService Settings, CheckRepositoryService Repo, OwnerAssignmentStore Owner) BuildDeps()
        {
            var dbPath = Path.Combine(_tempDir, "governance-history.db");

            var vaState = new VulnerabilityAssessmentStateService();
            // Over a temp file, not the operator's real %APPDATA%\SQLTriage\user-settings.json.
            // This service is only read here, but a test that binds a real profile still makes its
            // outcome depend on the developer's machine state — and this one is a singleton whose
            // setters save on every call (2026-08-04).
            var userSettings = new UserSettingsService(Path.Combine(_tempDir, "user-settings.json"));
            var blockingHistory = new BlockingHistoryService(
                NullLogger<BlockingHistoryService>.Instance,
                retentionDays: 30,
                dbPath: Path.Combine(_tempDir, "blocking-history.db"));
            var perfHistory = new HistoricalPerformanceService(
                NullLogger<HistoricalPerformanceService>.Instance,
                dbPath: dbPath);
            var govHistory = new GovernanceHistoryService(
                NullLogger<GovernanceHistoryService>.Instance,
                retentionDays: 365,
                dbDir: _tempDir);
            var connectionFactory = new NullDbConnectionFactory();
            var healthCheckSvc = new HealthCheckService(connectionFactory);

            var executiveHealth = new ExecutiveHealthService(
                healthCheckSvc,
                govHistory,
                blockingHistory,
                perfHistory,
                vaState,
                NullLogger<ExecutiveHealthService>.Instance);

            var checkRepo = new CheckRepositoryService(
                NullLogger<CheckRepositoryService>.Instance);

            return (executiveHealth, healthCheckSvc, vaState, userSettings, checkRepo, new OwnerAssignmentStore());
        }

        private (ReportBundleService Svc, VulnerabilityAssessmentStateService VaState) NewServiceWithState(AuditLogService? auditLog = null)
        {
            var d = BuildDeps();
            var svc = new ReportBundleService(
                d.Exec, d.Health, d.Va, d.Settings, d.Repo, d.Owner,
                NullLogger<ReportBundleService>.Instance, auditLog: auditLog);
            return (svc, d.Va);
        }

        /// <summary>Same construction, but the per-server Audit Evidence PDF build throws for one
        /// named server — the precondition platform-r2-06 could not induce with a real payload, so it
        /// is injected here to prove the estate zip REPORTS the dropped server instead of swallowing it.</summary>
        private (EstatePdfThrowingReportBundleService Svc, VulnerabilityAssessmentStateService VaState) NewThrowingService(string serverToThrowOn)
        {
            var d = BuildDeps();
            var svc = new EstatePdfThrowingReportBundleService(
                serverToThrowOn, d.Exec, d.Health, d.Va, d.Settings, d.Repo, d.Owner,
                NullLogger<ReportBundleService>.Instance);
            return (svc, d.Va);
        }

        /// <summary>Minimal VA finding, the shape GatherAuditEvidence reads (ThisServer/CheckId/
        /// Severity/DisplayName/Message) — enough for a non-empty attestation without a live scan.</summary>
        private static AssessmentResult Finding(string server, string checkId, string severity = "Warning") => new()
        {
            ThisServer = server,
            CheckId = checkId,
            DisplayName = $"{checkId} check",
            Severity = severity,
            Message = $"{checkId} finding on {server}",
            Status = "Failed",
        };

        // ── PrepareExecutiveSummaryHtml ───────────────────────────────────────

        [Fact]
        public async Task PrepareExecutiveSummaryHtml_ContainsHealthSection()
        {
            var svc = NewService();
            var html = await svc.PrepareExecutiveSummaryHtmlAsync("test-server");

            Assert.Contains("Overall Health Score", html, StringComparison.OrdinalIgnoreCase);
            // Ensure the HTML is non-trivially populated
            Assert.True(html.Length > 200, $"HTML too short ({html.Length} chars)");
        }

        // ── PrepareDbaHandoffHtml ─────────────────────────────────────────────

        [Fact]
        public async Task PrepareDbaHandoffHtml_IncludesAllSections()
        {
            var svc = NewService();
            var html = await svc.PrepareDbaHandoffHtmlAsync("test-server");

            // All four mandated sections
            Assert.Contains("Server Inventory", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("All Diagnostic Findings", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Known Issues", html, StringComparison.OrdinalIgnoreCase);
        }

        // ── PrepareAuditEvidenceHtml ──────────────────────────────────────────

        [Fact]
        public async Task PrepareAuditEvidenceHtml_IncludesChainStatus()
        {
            var svc = NewService();
            var html = await svc.PrepareAuditEvidenceHtmlAsync("test-server");

            // Chain status is always rendered (N/A when no AuditLogService provided)
            Assert.Contains("HMAC Chain Status", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Audit Evidence", html, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// THE HEADLINE DEFECT (2026-08-01 round 4). The Audit Evidence artifact read the STARTUP
        /// flags, and startup verification walks only the MOST-RECENT segment. So a chain whose
        /// older segments are demonstrably not intact printed a bare "Intact" on a document handed
        /// to a client's auditor. Measured that way on a read-only copy of the real production
        /// chain (startup flags all false, VerifyChain UNVERIFIABLE over 350 entries with 347
        /// unchecked) and reproduced on 791d2cb, so it is pre-existing and it is live.
        /// <para>
        /// Reproduced here with the same SHAPE and a sharper verdict: an older segment carries a
        /// tampered entry, the newest segment is clean. Startup sees nothing. The artifact must
        /// still not say Intact, and must not disagree with VerifyChain about the same chain.
        /// </para>
        /// <para>RED at 0e3fad0: the HTML prints "Intact" while VerifyChain reports BROKEN.</para>
        /// </summary>
        [Fact]
        public async Task AuditEvidenceHtml_DoesNotPrintIntact_WhenAnOlderSegmentIsTampered()
        {
            if (!OperatingSystem.IsWindows()) return; // the anchor and key are DPAPI-wrapped

            var auditDir = Path.Combine(_tempDir, "audit-logs");
            Directory.CreateDirectory(auditDir);

            // Launch 1 — a segment that will become the OLDER one.
            string firstSegment;
            using (var audit = new AuditLogService(auditDir, startFlushTimer: false))
            {
                audit.LogConnectionAttempt("srv1", success: true);
                audit.LogConnectionAttempt("srv2", success: true);
                audit.Flush();
                firstSegment = Directory.GetFiles(auditDir, "audit-*.jsonl").Single();
            }

            // Age it by a day so the next launch opens a fresh segment. The chain link survives:
            // startup seeds its tail from the LAST segment chronologically, whatever it is named.
            // Segment names are audit-yyyy-MM-dd.jsonl and are ordered by ORDINAL name sort — the
            // dashes matter, "audit-20260731" would sort AFTER "audit-2026-08-01" and quietly make
            // the aged file the newest segment instead of the oldest.
            var agedName = "audit-" + DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd") + ".jsonl";
            File.Move(firstSegment, Path.Combine(auditDir, agedName));

            // Launch 2 — writes the newest segment, which stays clean throughout.
            using (var audit = new AuditLogService(auditDir, startFlushTimer: false))
            {
                audit.LogConnectionAttempt("srv3", success: true);
                audit.Flush();
            }

            // Tamper an entry in the OLDER segment only.
            var agedPath = Path.Combine(auditDir, agedName);
            var lines = File.ReadAllLines(agedPath);
            lines[0] = lines[0].Replace("\"Message\":\"", "\"Message\":\"TAMPERED ");
            Assert.Contains("TAMPERED", lines[0]);
            File.WriteAllLines(agedPath, lines);

            using var auditLog = new AuditLogService(auditDir, startFlushTimer: false);

            // The startup surface — which is what the artifact used to read — sees nothing at all.
            Assert.False(auditLog.ChainBroken,
                "Precondition: the defect only shows when the newest segment is clean.");
            Assert.False(auditLog.ChainUnverifiable);

            var html = await NewService(auditLog).PrepareAuditEvidenceHtmlAsync("test-server");

            var verdict = auditLog.VerifyChain("test").StatusLabel;
            Assert.Equal("BROKEN", verdict);

            Assert.DoesNotContain("HMAC Chain Status</th><td class=\"chain-intact\"", html);
            Assert.Contains("chain-broken", html);
            Assert.Contains("Broken", html, StringComparison.Ordinal);
        }

        // ── The two ANCHOR fold branches (2026-08-01 round 5) ─────────────────
        //
        // Round 4 added the fold (a truncated chain whose survivors all verify is still not intact;
        // an unreadable anchor means truncation can be neither detected nor ruled out) and shipped
        // BOTH branches with no test at all. They are the branches that make the client-facing
        // verdict differ from a bare VerifyChain, so they are precisely the ones that must not rot.

        /// <summary>
        /// Records removed, the anchor still in place. Every surviving entry verifies, so VerifyChain
        /// says INTACT and is right about what is on disk — the anchor is the only witness that
        /// anything is missing. The bundle must not print Intact over that.
        /// </summary>
        [Fact]
        public async Task AuditEvidenceHtml_DoesNotPrintIntact_WhenTheAnchorShowsRecordsWereRemoved()
        {
            if (!OperatingSystem.IsWindows()) return; // the anchor is DPAPI-wrapped

            var auditDir = Path.Combine(_tempDir, "audit-logs");
            Directory.CreateDirectory(auditDir);

            string segment;
            using (var audit = new AuditLogService(auditDir, startFlushTimer: false))
            {
                for (int i = 0; i < 4; i++) audit.LogConnectionAttempt($"srv{i}", success: true);
                audit.Flush(); // anchors the tail signature
                segment = Directory.GetFiles(auditDir, "audit-*.jsonl").Single();
            }

            var lines = File.ReadAllLines(segment);
            Assert.Equal(4, lines.Length);
            File.WriteAllLines(segment, lines.Take(3).ToArray());

            using var auditLog = new AuditLogService(auditDir, startFlushTimer: false);

            // What a bare VerifyChain says about these exact bytes — and it is not wrong, it simply
            // cannot see a record that is no longer there.
            Assert.Equal("INTACT", auditLog.VerifyChain("precondition").StatusLabel);

            var html = await NewService(auditLog).PrepareAuditEvidenceHtmlAsync("test-server");

            Assert.Contains("HMAC Chain Status</th><td class=\"chain-broken\"", html);
            Assert.DoesNotContain("HMAC Chain Status</th><td class=\"chain-intact\"", html);
            Assert.Contains("REMOVED", html, StringComparison.Ordinal);
        }

        /// <summary>
        /// The anchor is present but cannot be read, over a chain nothing touched — what a
        /// service-account change leaves behind. Truncation can then be neither detected nor ruled
        /// out, so the bundle must claim neither: not Intact, and not tampering either.
        /// </summary>
        [Fact]
        public async Task AuditEvidenceHtml_IsIndeterminate_WhenTheAnchorCannotBeRead()
        {
            if (!OperatingSystem.IsWindows()) return;

            var auditDir = Path.Combine(_tempDir, "audit-logs");
            Directory.CreateDirectory(auditDir);

            using (var audit = new AuditLogService(auditDir, startFlushTimer: false))
            {
                audit.LogConnectionAttempt("srv1", success: true);
                audit.LogConnectionAttempt("srv2", success: true);
                audit.Flush();
            }

            var anchorPath = Path.Combine(auditDir, ".chain-anchor");
            Assert.True(File.Exists(anchorPath));
            File.SetAttributes(anchorPath, FileAttributes.Normal);
            File.WriteAllBytes(anchorPath, System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes("{\"Sig\":\"x\",\"Ts\":\"x\"}"),
                System.Text.Encoding.UTF8.GetBytes("a-different-entropy"),
                System.Security.Cryptography.DataProtectionScope.LocalMachine));

            using var auditLog = new AuditLogService(auditDir, startFlushTimer: false);

            Assert.Equal("INTACT", auditLog.VerifyChain("precondition").StatusLabel);
            Assert.False(auditLog.ChainBroken,
                "An anchor this identity cannot read is an evidence failure, not evidence of an attack.");

            var html = await NewService(auditLog).PrepareAuditEvidenceHtmlAsync("test-server");

            // Assert on the CELL, not on bare class names — every class name also appears in the
            // stylesheet, so a substring test over the whole document proves nothing.
            Assert.Contains("HMAC Chain Status</th><td class=\"chain-indeterminate\"", html);
            Assert.DoesNotContain("HMAC Chain Status</th><td class=\"chain-intact\"", html);
            Assert.DoesNotContain("HMAC Chain Status</th><td class=\"chain-broken\"", html);
        }

        /// <summary>
        /// The stylesheet must carry a rule for EVERY verdict the artifact can emit. Measured on a
        /// generated bundle 2026-08-01: .chain-intact and .chain-broken existed, .chain-unverifiable
        /// and .chain-indeterminate did not — so the one clean verdict rendered green and bold while
        /// the two verdicts the runbook says must not be closed without independent evidence
        /// rendered as ordinary body text. A compliance document that styles only reassurance.
        /// </summary>
        /// <remarks>
        /// It came back on 2026-08-11, on a fifth verdict, because this test asserted a HAND-WRITTEN
        /// list of five class names: a restart-only chain emitted <c>class="chain-restarted"</c>
        /// into a document with no such rule and the test stayed green, since a written list cannot
        /// miss a name it was never given. The class the artifact emits is
        /// <c>AuditLogService.StatusLabelFor(status).ToLowerInvariant()</c>, so the list is now
        /// DERIVED from the status enum. A sixth verdict fails here instead of shipping unstyled.
        /// </remarks>
        [Fact]
        public async Task AuditEvidenceHtml_StylesEveryChainVerdict_NotJustTheCleanOne()
        {
            var html = await NewService().PrepareAuditEvidenceHtmlAsync("test-server");

            foreach (AuditLogService.ChainVerificationStatus status in
                     Enum.GetValues<AuditLogService.ChainVerificationStatus>())
            {
                var cls = ".chain-" + AuditLogService.StatusLabelFor(status).ToLowerInvariant();
                Assert.Contains(cls + " {", html, StringComparison.Ordinal);
            }

            // The one class that has no status behind it: the cell reads "Could not verify" when
            // verification threw, and it must not render as body text either.
            Assert.Contains(".chain-unknown {", html, StringComparison.Ordinal);
        }

        // ── PendingHtml is stored ─────────────────────────────────────────────

        [Fact]
        public async Task PrepareExecutiveSummaryHtml_StoresInPendingHtml()
        {
            var svc = NewService();
            var html = await svc.PrepareExecutiveSummaryHtmlAsync("test-server");

            Assert.True(svc.PendingHtml.ContainsKey("test-server|ExecutiveSummary"),
                "HTML should be stored under 'server|ExecutiveSummary' key.");
            Assert.Equal(html, svc.PendingHtml["test-server|ExecutiveSummary"]);
        }

        // ── BuildAuditEvidencePdfAsync (the PDF path — ZERO coverage before this: only the HTML
        //    twin above was tested. QuestPDF, pure and headless — see AssessmentPdf.BuildAuditEvidenceBundle,
        //    called from ReportBundleService.cs:598-655.) ─────────────────────

        [Fact]
        public async Task BuildAuditEvidencePdfAsync_ProducesNonEmptyValidPdf()
        {
            var (svc, vaState) = NewServiceWithState();
            vaState.Results = new List<AssessmentResult> { Finding("test-server", "VA1001") };
            vaState.HasRun = true;

            var bytes = await svc.BuildAuditEvidencePdfAsync("test-server", watermark: false);

            Assert.True(bytes.Length > 200, $"PDF too small ({bytes.Length} bytes)");
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        }

        /// <summary>
        /// Title/Author/Producer/Creator — <see cref="AssessmentPdf.BuildPdfMetadata"/>, applied
        /// 2026-08-20. Before this every generated PDF's document metadata was blank at all 14
        /// GeneratePdf() call sites (grepped — zero hits for QuestPDF metadata anywhere in the repo).
        /// </summary>
        [Fact]
        public async Task BuildAuditEvidencePdfAsync_SetsDocumentMetadata()
        {
            var (svc, vaState) = NewServiceWithState();
            vaState.Results = new List<AssessmentResult> { Finding("test-server", "VA1001") };
            vaState.HasRun = true;

            var bytes = await svc.BuildAuditEvidencePdfAsync("test-server", watermark: false);
            // QuestPDF's /Info dictionary is plain literal text (not compressed) — confirmed by
            // generating a sample document and inspecting the raw bytes; a byte/string scan is a
            // valid way to assert on it, it does not require a PDF parser.
            var text = System.Text.Encoding.Latin1.GetString(bytes);

            Assert.Contains("/Title (Audit Evidence)", text, StringComparison.Ordinal);
            Assert.Contains("/Author (SQLTriage", text, StringComparison.Ordinal);
            Assert.Contains("/Creator (SQLTriage", text, StringComparison.Ordinal);
            Assert.Contains("/Producer (SQLTriage", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// The doc/scan-data SHA-256 mirrored into the PDF's own metadata (Keywords) — item 3's
        /// other half. Reads the expected hash off the HTML twin rather than restating
        /// GatherAuditEvidence's formula here: both paths call the SAME frozen method
        /// (ReportBundleService.cs:360-376) with the same input on the same calendar day, so they
        /// must agree, and if they ever silently diverge this test is a tripwire.
        /// </summary>
        [Fact]
        public async Task BuildAuditEvidencePdfAsync_MirrorsTheAlreadyComputedShaIntoMetadata()
        {
            var (svc, vaState) = NewServiceWithState();
            vaState.Results = new List<AssessmentResult> { Finding("test-server", "VA1001") };
            vaState.HasRun = true;

            var html = await svc.PrepareAuditEvidenceHtmlAsync("test-server");
            var match = System.Text.RegularExpressions.Regex.Match(html, "Document SHA-256: ([0-9a-f]{64})");
            Assert.True(match.Success, "Could not find the Document SHA-256 line in the HTML twin.");
            var docSha = match.Groups[1].Value;

            var bytes = await svc.BuildAuditEvidencePdfAsync("test-server", watermark: false);
            var text = System.Text.Encoding.Latin1.GetString(bytes);

            Assert.Contains($"doc-sha256:{docSha}", text, StringComparison.Ordinal);
        }

        // ── platform-r2-05 (#10 top-ten): an empty attestation zip reported clean ─────────────
        // The CLI's emptiness guard checks allResults.Count. But the zip is built from
        // ScannedServers() (results with a non-null ThisServer). When the identity query fails,
        // ThisServer is null on every result: results EXIST but ScannedServers() is empty and the zip
        // has zero entries. HasVaResultsFor(null) is the enforcing seam the CLI now consults.

        private static int ZipEntryCount(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            return zip.Entries.Count;
        }

        [Fact]
        public async Task NullIdentityResults_YieldEmptyAttestation_AndTheSeamSaysSo()
        {
            var (svc, vaState) = NewServiceWithState();
            // The exact state the scout reproduced: results exist, but the identity query failed so
            // every ThisServer is null.
            vaState.Results = new List<AssessmentResult>
            {
                new() { ThisServer = null, CheckId = "VA1001", DisplayName = "VA1001 check",
                        Severity = "High", Message = "finding with no server identity", Status = "Failed" },
            };
            vaState.HasRun = true;

            Assert.NotEmpty(vaState.Results);                 // results DO exist (the old guard passed)
            Assert.Empty(svc.ScannedServers());               // …but nothing carries an identity
            Assert.False(svc.HasVaResultsFor(null));          // the seam the CLI now refuses on

            var bytes = await svc.BuildAuditEvidenceEstateZipAsync(watermarkAll: false);
            Assert.Equal(0, ZipEntryCount(bytes));            // a zip that attests nothing
        }

        [Fact]
        public async Task IdentifiedResults_ProduceANonEmptyAttestation()
        {
            var (svc, vaState) = NewServiceWithState();
            vaState.Results = new List<AssessmentResult> { Finding("SQL01", "VA1001") };
            vaState.HasRun = true;

            Assert.True(svc.HasVaResultsFor(null));
            var bytes = await svc.BuildAuditEvidenceEstateZipAsync(watermarkAll: false);
            Assert.Equal(1, ZipEntryCount(bytes));
        }

        // ── platform-r2-06: a per-server PDF that throws is dropped from the estate zip ───────
        [Fact]
        public async Task EstateZip_ReportsServersDroppedWhenTheirPerServerPdfThrows()
        {
            var (svc, vaState) = NewThrowingService(serverToThrowOn: "SQL02");
            vaState.Results = new List<AssessmentResult>
            {
                Finding("SQL01", "VA1001"),
                Finding("SQL02", "VA1002"),
            };
            vaState.HasRun = true;

            var skipped = new List<string>();
            var bytes = await svc.BuildAuditEvidenceEstateZipAsync(watermarkAll: false, skipped);

            // The good server is attested; the throwing one is dropped AND named, not swallowed.
            Assert.Equal(1, ZipEntryCount(bytes));
            Assert.Equal(new[] { "SQL02" }, skipped);
        }

        [Fact]
        public async Task EstateZip_ReportsNothingSkipped_WhenAllServersSucceed()
        {
            var (svc, vaState) = NewThrowingService(serverToThrowOn: "does-not-exist");
            vaState.Results = new List<AssessmentResult>
            {
                Finding("SQL01", "VA1001"),
                Finding("SQL02", "VA1002"),
            };
            vaState.HasRun = true;

            var skipped = new List<string>();
            var bytes = await svc.BuildAuditEvidenceEstateZipAsync(watermarkAll: false, skipped);

            Assert.Equal(2, ZipEntryCount(bytes));
            Assert.Empty(skipped);
        }
    }

    /// <summary>
    /// A ReportBundleService whose per-server Audit Evidence PDF build throws for one named server —
    /// the platform-r2-06 precondition, injected because no real payload could induce a QuestPDF throw.
    /// </summary>
    internal sealed class EstatePdfThrowingReportBundleService : ReportBundleService
    {
        private readonly string _throwOn;

        public EstatePdfThrowingReportBundleService(
            string throwOn, ExecutiveHealthService exec, HealthCheckService health,
            VulnerabilityAssessmentStateService va, UserSettingsService settings,
            CheckRepositoryService repo, OwnerAssignmentStore owner, ILogger<ReportBundleService> logger)
            : base(exec, health, va, settings, repo, owner, logger)
        {
            _throwOn = throwOn;
        }

        public override Task<byte[]> BuildAuditEvidencePdfAsync(string serverName, bool watermark)
        {
            if (serverName.Equals(_throwOn, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("forced per-server PDF failure for platform-r2-06 test");
            return base.BuildAuditEvidencePdfAsync(serverName, watermark);
        }
    }

    /// <summary>
    /// Minimal IDbConnectionFactory that always throws (no live connections needed in these tests).
    /// </summary>
    internal sealed class NullDbConnectionFactory : IDbConnectionFactory
    {
        public string DataSourceType => "none";

        public System.Data.IDbConnection CreateConnection()
            => throw new InvalidOperationException("No SQL connection available in unit tests.");

        public System.Threading.Tasks.Task<System.Data.IDbConnection> CreateConnectionAsync()
            => throw new InvalidOperationException("No SQL connection available in unit tests.");
    }
}
