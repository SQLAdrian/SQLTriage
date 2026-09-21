/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:ReportBundleService.Class — one-click generation of Executive Summary, DBA Handoff, and Audit Evidence PDF bundles
    /// <summary>
    /// Generates diagnostic report bundles (Executive Summary, DBA Handoff, Audit Evidence, Risk
    /// Register/Acknowledgement, HA/DR Posture). Two independent output paths — neither is a render
    /// of the other:
    ///  - HTML: composed as plain strings, cached in <see cref="PendingHtml"/>, and written straight
    ///    to a .html file in the user's Downloads folder by the caller
    ///    (Pages/ReportBundles.razor's GenerateAsync). No browser, no print step.
    ///  - PDF: built server-side and headlessly with QuestPDF (the Build*PdfAsync methods below,
    ///    via AssessmentPdf), then saved to .\output next to the exe
    ///    (Pages/ReportBundles.razor's GeneratePdfAsync). This REPLACED an earlier browser-print
    ///    flow — a print iframe captured by PrintService.PrintToPdfAsync — which this class no
    ///    longer drives for any bundle.
    /// </summary>
    public class ReportBundleService
    {
        private readonly ExecutiveHealthService _executiveHealth;
        private readonly HealthCheckService _healthCheckService;
        private readonly VulnerabilityAssessmentStateService _vaState;
        private readonly AuditLogService? _auditLog;
        private readonly UserSettingsService _userSettings;
        private readonly CheckRepositoryService _checkRepo;
        private readonly OwnerAssignmentStore _ownerStore;
        private readonly CheckExecutionService? _checkExecution;
        private readonly ILogger<ReportBundleService> _logger;

        /// <summary>
        /// Tracks the last-generated timestamp per server+bundle type during the session.
        /// Key: "{serverName}|{bundleType}"
        /// </summary>
        public ConcurrentDictionary<string, DateTime> LastGenerated { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stores the most recently composed HTML for each bundle type.
        /// Key: "{serverName}|{bundleType}" — consumed by the print page.
        /// </summary>
        public ConcurrentDictionary<string, string> PendingHtml { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ReportBundleService(
            ExecutiveHealthService executiveHealth,
            HealthCheckService healthCheckService,
            VulnerabilityAssessmentStateService vaState,
            UserSettingsService userSettings,
            CheckRepositoryService checkRepo,
            OwnerAssignmentStore ownerStore,
            ILogger<ReportBundleService> logger,
            AuditLogService? auditLog = null,
            CheckExecutionService? checkExecution = null)
        {
            _executiveHealth = executiveHealth ?? throw new ArgumentNullException(nameof(executiveHealth));
            _healthCheckService = healthCheckService ?? throw new ArgumentNullException(nameof(healthCheckService));
            _vaState = vaState ?? throw new ArgumentNullException(nameof(vaState));
            _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
            _checkRepo = checkRepo ?? throw new ArgumentNullException(nameof(checkRepo));
            _ownerStore = ownerStore ?? throw new ArgumentNullException(nameof(ownerStore));
            _checkExecution = checkExecution;
            _logger = logger;
            _auditLog = auditLog;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Bundle entry points
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Composes the Executive Summary HTML and caches it in <see cref="PendingHtml"/>.
        /// Returns the same string — the caller (Pages/ReportBundles.razor) writes it straight to a
        /// .html file; nothing renders or prints it.
        /// </summary>
        public async Task<string> PrepareExecutiveSummaryHtmlAsync(string serverName)
        {
            var display = AnonymisedName(serverName);
            var health = await _executiveHealth.GetHealthScoreAsync(serverName).ConfigureAwait(false);
            var allFindings = GetMergedFindings(serverName);
            var top5 = allFindings
                .Where(f => f.ThisServer == null || f.ThisServer.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => SeverityRank(f.Severity))
                .Take(5)
                .Select(ToBundleFinding)
                .ToList();

            var sb = new StringBuilder();
            sb.Append(HtmlHead("Executive Summary"));
            sb.Append($"""
                <div class="rb-header">
                    <div class="rb-tag">Executive Summary — for non-technical stakeholders</div>
                    <h1>{EscapeHtml(display)}</h1>
                    <div class="rb-meta">Reflects the most recent completed assessment &nbsp;|&nbsp; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
                </div>
                """);

            // Health score block. Gate fix C4 (2026-08-05): this printed health.Score straight,
            // and ExecutiveHealthScore.Score holds 0 in every state where nothing was measured.
            // A client whose server had gone silent opened an "Executive Summary" headed
            // "Overall Health Score 0 / 100" in a red band, with an empty message under it, and
            // nothing anywhere on the page said no measurement stood behind the number. Read
            // MeasuredScore, which is null in exactly those states, and print the policy's
            // sentence where the number would have gone.
            var measured = health.MeasuredScore;
            if (measured.HasValue)
            {
                var scoreClass = measured.Value >= 70 ? "score-good" : measured.Value >= 40 ? "score-warn" : "score-bad";
                sb.Append($"""
                    <section>
                        <h2>Overall Health Score</h2>
                        <div class="score-block {scoreClass}">
                            <span class="score-number">{measured.Value}</span><span class="score-label"> / 100</span>
                            <div class="score-msg">{EscapeHtml(health.Message ?? string.Empty)}</div>
                        </div>
                    </section>
                    """);
            }
            else
            {
                sb.Append($"""
                    <section>
                        <h2>Overall Health Score</h2>
                        <div class="score-block score-unknown">
                            <span class="score-number">n/a</span>
                            <div class="score-msg">{EscapeHtml(EstateHealthPolicy.ServerBasis(health))}</div>
                        </div>
                    </section>
                    """);
            }

            // Top 5 risks
            sb.Append("<section><h2>Top 5 Risks</h2>");
            if (top5.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No cached corpus findings. Run the check suite (Audit Assessment) first.</p>");
            }
            else
            {
                // reports-r2-02: the Status column mirrors the PDF's, so the two deliverables built
                // from one dataset cannot disagree about whether a row is a finding or a check that
                // could not run.
                sb.Append("<table class=\"rb-table\"><thead><tr><th>Severity</th><th>Status</th><th>Check</th><th>Category</th><th>Message</th></tr></thead><tbody>");
                foreach (var f in top5)
                {
                    var impact = string.IsNullOrWhiteSpace(f.BusinessImpact) ? f.Message : f.BusinessImpact;
                    sb.Append($"<tr><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(StatusLabel(f.Status))}</td><td>{EscapeHtml(f.Name)}</td><td>{EscapeHtml(f.Category)}</td><td>{EscapeHtml(impact)}</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");

            // Trend placeholder
            sb.Append("""
                <section>
                    <h2>30-Day Trend</h2>
                    <p class="rb-placeholder">Trend graph available when historical wait stats data is present. Navigate to Performance Trends to view.</p>
                </section>
                """);

            sb.Append(HtmlFoot());
            var html = sb.ToString();
            var key = BundleKey(serverName, "ExecutiveSummary");
            PendingHtml[key] = html;
            return html;
        }

        // ─────────────────────────────────────────────────────────────────────
        // All-Servers (estate) variants — roll-up by check across the scanned set.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Estate-wide Executive Summary: top risks ranked by servers impacted.</summary>
        public Task<string> PrepareExecutiveSummaryEstateHtmlAsync()
        {
            var (rollup, _, serverCount, coverageNote) = GatherEstateRollup();
            var topRisks = rollup.Take(5).ToList();

            var sb = new StringBuilder();
            sb.Append(HtmlHead("Executive Summary — Estate"));
            sb.Append($"""
                <div class="rb-header">
                    <div class="rb-tag">Executive Summary — estate roll-up across {serverCount} server{(serverCount == 1 ? "" : "s")}</div>
                    <h1>All Servers</h1>
                    {CoverageHtml(coverageNote)}
                    <div class="rb-meta">Reflects the most recent completed assessment &nbsp;|&nbsp; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
                </div>
                """);

            sb.Append("<section><h2>Top 5 Risks Across the Estate</h2>");
            if (topRisks.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No cached corpus findings. Run the check suite (Audit Assessment) first.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>Severity</th><th>Status</th><th>Check</th><th>Category</th><th>Servers Impacted</th></tr></thead><tbody>");
                foreach (var f in topRisks)
                {
                    sb.Append($"<tr><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(StatusLabel(f.Status))}</td><td>{EscapeHtml(f.Name)}</td><td>{EscapeHtml(f.Category)}</td><td>{EscapeHtml(f.ServersImpactedLabel)}</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");

            sb.Append(HtmlFoot());
            return Task.FromResult(sb.ToString());
        }

        /// <summary>
        /// Composes the DBA Handoff Package HTML and queues it for printing.
        /// </summary>
        public async Task<string> PrepareDbaHandoffHtmlAsync(string serverName)
        {
            var display = AnonymisedName(serverName);
            var health = _healthCheckService.GetCachedHealth(serverName);
            var allFindings = GetMergedFindings(serverName)
                .Where(f => f.ThisServer == null || f.ThisServer.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => SeverityRank(f.Severity))
                .Select(ToBundleFinding)
                .ToList();

            var sb = new StringBuilder();
            sb.Append(HtmlHead("DBA Handoff Package"));
            sb.Append($"""
                <div class="rb-header">
                    <div class="rb-tag">DBA Handoff — full diagnostic baseline</div>
                    <h1>{EscapeHtml(display)}</h1>
                    <div class="rb-meta">Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
                </div>
                """);

            // Server inventory
            sb.Append("<section><h2>Server Inventory</h2>");
            if (health != null)
            {
                sb.Append($"""
                    <table class="rb-table rb-kv">
                        <tr><th>Server Name</th><td>{EscapeHtml(health.ServerName)}</td></tr>
                        <tr><th>Online</th><td>{(health.IsOnline == true ? "Yes" : "No")}</td></tr>
                        <tr><th>CPU Usage</th><td>{health.CpuPercent?.ToString() ?? "—"} %</td></tr>
                        <tr><th>Buffer Pool (MB)</th><td>{health.BufferPoolMb?.ToString("N0") ?? "—"}</td></tr>
                        <tr><th>Top Wait Type</th><td>{EscapeHtml(health.TopWaitType ?? "—")}</td></tr>
                        <tr><th>Last Checked</th><td>{health.LastUpdated?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"}</td></tr>
                    </table>
                    """);
            }
            else
            {
                sb.Append("<p class=\"rb-empty\">No health data cached. Visit the Health page first.</p>");
            }
            sb.Append("</section>");

            // All findings (corpus check results only — honesty ruling 2026-07-16, Builder A:
            // this no longer unions Microsoft VA; VA lives only on its own page)
            sb.Append("<section><h2>All Diagnostic Findings</h2>");
            if (allFindings.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No cached findings. Run the check suite (Audit Assessment) first.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>ID</th><th>Severity</th><th>Status</th><th>Check</th><th>Category</th><th>Source</th><th>Message</th></tr></thead><tbody>");
                foreach (var f in allFindings)
                {
                    // 2026-07-22: Message is raw corpus Markdown. Escaping it published literal "**"
                    // and "### Why this matters" into the HTML deliverable, the same defect the PDF had.
                    // Flatten to plain text first (still escaped -- this is not a licence to inject HTML).
                    sb.Append($"<tr><td>{EscapeHtml(f.Id)}</td><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(StatusLabel(f.Status))}</td><td>{EscapeHtml(f.Name)}</td><td>{EscapeHtml(f.Category)}</td><td>{EscapeHtml(f.Source)}</td><td>{EscapeHtml(PlainText(f.Message))}</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");

            // Known issues (failed checks)
            var failed = allFindings.Where(f => f.Status?.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true
                || f.Severity?.Equals("Error", StringComparison.OrdinalIgnoreCase) == true).ToList();
            sb.Append("<section><h2>Known Issues (Failed Checks)</h2>");
            if (failed.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No failed checks in cached findings.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>ID</th><th>Check</th><th>Remediation</th></tr></thead><tbody>");
                foreach (var f in failed)
                {
                    sb.Append($"<tr><td>{EscapeHtml(f.Id)}</td><td>{EscapeHtml(f.Name)}</td><td>{EscapeHtml(f.Remediation)}</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");

            sb.Append(HtmlFoot());
            var html = sb.ToString();
            var key = BundleKey(serverName, "DbaHandoff");
            PendingHtml[key] = html;
            await Task.CompletedTask.ConfigureAwait(false);
            return html;
        }

        /// <summary>Estate-wide DBA Handoff: roll-up table grouped by check + per-server appendix.</summary>
        public Task<string> PrepareDbaHandoffEstateHtmlAsync()
        {
            var (rollup, appendix, serverCount, coverageNote) = GatherEstateRollup();

            var sb = new StringBuilder();
            sb.Append(HtmlHead("DBA Handoff Package — Estate"));
            sb.Append($"""
                <div class="rb-header">
                    <div class="rb-tag">DBA Handoff — estate baseline across {serverCount} server{(serverCount == 1 ? "" : "s")}</div>
                    <h1>All Servers</h1>
                    {CoverageHtml(coverageNote)}
                    <div class="rb-meta">Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
                </div>
                """);

            // Roll-up: one row per check, with the impacted-server count (NOT a per-server name list).
            sb.Append("<section><h2>Findings Across the Estate (grouped by check)</h2>");
            if (rollup.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No cached findings. Run the check suite (Audit Assessment) first.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>ID</th><th>Severity</th><th>Status</th><th>Check</th><th>Category</th><th>Source</th><th>Servers Impacted</th></tr></thead><tbody>");
                foreach (var f in rollup)
                {
                    sb.Append($"<tr><td>{EscapeHtml(f.Id)}</td><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(StatusLabel(f.Status))}</td><td>{EscapeHtml(f.Name)}</td><td>{EscapeHtml(f.Category)}</td><td>{EscapeHtml(f.Source)}</td><td>{EscapeHtml(f.ServersImpactedLabel)}</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");

            // Appendix: full per-server detail, once each — the names that would otherwise bloat the rows above.
            sb.Append("<section><h2>Appendix — Failed Checks by Server</h2>");
            if (appendix.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No failed checks across the estate.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>Server</th><th>Failed</th><th>Check IDs</th></tr></thead><tbody>");
                foreach (var s in appendix)
                {
                    sb.Append($"<tr><td>{EscapeHtml(s.Server)}</td><td>{s.FindingCount}</td><td>{EscapeHtml(string.Join(", ", s.CheckIds))}</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");

            sb.Append(HtmlFoot());
            return Task.FromResult(sb.ToString());
        }

        /// <summary>
        /// Composes the Audit Evidence HTML and queues it for printing.
        /// </summary>
        /// <summary>
        /// Gathers the audit-evidence findings for a server and computes the two integrity hashes:
        /// a document hash over the rendered (enriched) findings, and a scan-data hash over the raw
        /// assessment results (load-path / build-profile independent). Shared by the HTML and PDF paths.
        /// </summary>
        private (List<BundleFinding> Enriched, string DocSha, string ScanSha) GatherAuditEvidence(string serverName, string display)
        {
            var rawFindings = GetCachedFindings()
                .Where(f => f.ThisServer == null || f.ThisServer.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => SeverityRank(f.Severity))
                .ToList();

            var enriched = rawFindings.Select(ToBundleFinding).ToList();
            var today = $"|{display}|{DateTime.Now:yyyy-MM-dd}";

            // Document hash: exactly what's rendered (re-derivable from the visible report).
            var docText = string.Join("\n", enriched.Select(f => $"{f.Id}|{f.Severity}|{f.Name}|{f.Message}"));
            // Scan-data hash: raw assessment fields, stable across corpus enrichment / build profiles.
            var scanText = string.Join("\n", rawFindings.Select(f => $"{f.CheckId}|{f.Severity}|{f.DisplayName}|{f.Message}"));

            return (enriched, ComputeSha256(docText + today), ComputeSha256(scanText + today));
        }

        public async Task<string> PrepareAuditEvidenceHtmlAsync(string serverName)
        {
            var display = AnonymisedName(serverName);
            var (allFindings, sha256, scanSha256) = GatherAuditEvidence(serverName, display);

            // Audit log summary: count events in last 30 days.
            // reports-r2-09 (2026-08-27): null = not measured (no audit service, or the read threw).
            // The PDF twin makes the same distinction — the two deliverables must not disagree about
            // whether a number exists.
            int? auditCount = null;
            var chainStatus = "N/A (audit service not available)";
            var chainStatusClass = "unknown";
            if (_auditLog != null)
            {
                try
                {
                    var from = DateTime.Now.AddDays(-30);
                    var to = DateTime.Now;
                    var entries = _auditLog.GetEntries(from, to);
                    auditCount = entries.Count;
                    // Four-state (2026-08-01, round 3), FULL SCOPE (round 4).
                    //
                    // This used to read the STARTUP flags (ChainBroken / ChainProvenanceIndeterminate
                    // / ChainUnverifiable), and startup verification walks only the MOST-RECENT
                    // segment. Measured 2026-08-01 on a read-only, byte-verified copy of the
                    // installed service's production chain: all three flags false, so this artifact
                    // printed a bare "Intact", while VerifyChain on the same launch reported
                    // UNVERIFIABLE with 357 of 363 retained entries unchecked. (One figure, dated,
                    // and it is a snapshot of a growing chain — see the remarks on
                    // DescribeChainForCompliance.) A compliance document is the last place a partial
                    // scan may be reported as a whole one. Reproduced on 791d2cb, so the defect is
                    // pre-existing and it is live.
                    //
                    // A bundle is generated on demand, so it now runs its own FULL verification and
                    // prints a statement that carries its own scope. AuditLogService owns the wording
                    // (DescribeChainForCompliance) so the HTML and the PDF cannot drift apart.
                    //
                    // What that buys, stated as measured and not more (corrected round 5 — it read
                    // "so this artifact and VerifyChain cannot disagree about the same chain", which
                    // measurement falsified twice): the fold is ESCALATE-ONLY, so this artifact may
                    // print a WORSE verdict than a bare VerifyChain over the same bytes, naming the
                    // out-of-band anchor evidence VerifyChain structurally cannot see. It can never
                    // print a better one. That is the guarantee — not agreement, but no unearned
                    // reassurance.
                    var statement = _auditLog.DescribeChainForCompliance(
                        "report-bundle:audit-evidence-html");
                    chainStatus = statement.Text;
                    chainStatusClass = statement.Label.ToLowerInvariant();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read audit log entries for Audit Evidence report");
                    chainStatus = "Could not verify";
                }
            }

            var sb = new StringBuilder();
            sb.Append(HtmlHead("Audit Evidence"));
            sb.Append($"""
                <div class="rb-header">
                    <div class="rb-tag">Audit Evidence — for compliance review</div>
                    <h1>{EscapeHtml(display)}</h1>
                    <div class="rb-meta">Reflects the most recent completed assessment &nbsp;|&nbsp; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
                    <div class="rb-sha">Document SHA-256: {sha256}</div>
                    <div class="rb-sha">Scan-data SHA-256: {scanSha256}</div>
                </div>
                """);

            // VA findings with framework hooks. Audit Evidence is deliberately a SINGLE-SOURCE
            // Microsoft VA attestation (its own verifiable hash) — corpus findings are surfaced
            // in the operational bundles, never blended into this compliance hash.
            sb.Append("<section><h2>Vulnerability Assessment Findings (Microsoft VA)</h2>");
            if (allFindings.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No cached findings. Run a Vulnerability Scan first.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>ID</th><th>Severity</th><th>Check</th><th>Category</th><th>Framework</th><th>Message</th></tr></thead><tbody>");
                foreach (var f in allFindings)
                {
                    sb.Append($"<tr><td>{EscapeHtml(f.Id)}</td><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(f.Name)}</td><td>{EscapeHtml(f.Category)}</td><td>{EscapeHtml(f.Framework)}</td><td>{EscapeHtml(f.Message)}</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");

            // Audit log summary
            var auditCountText = auditCount.HasValue
                ? auditCount.Value.ToString("N0")
                : "Not measured: the audit log could not be read";
            sb.Append($"""
                <section>
                    <h2>Audit Log Summary (Last 30 Days)</h2>
                    <table class="rb-table rb-kv">
                        <tr><th>Total Audit Events</th><td>{EscapeHtml(auditCountText)}</td></tr>
                        <tr><th>HMAC Chain Status</th><td class="chain-{chainStatusClass}">{EscapeHtml(chainStatus)}</td></tr>
                    </table>
                </section>
                """);

            // Signature block.
            // reports-r1-03 (2026-08-27): the "Report Period / Last 30 days" row is gone from both
            // twins. GatherAuditEvidence applies no date filter, so the window described nothing
            // the hashed body was selected by. The PDF twin carries the identical replacement.
            sb.Append($"""
                <section class="rb-signature">
                    <h2>Report Integrity</h2>
                    <table class="rb-table rb-kv">
                        <tr><th>Generated By</th><td>SQLTriage Diagnostic Report Packages v1</td></tr>
                        <tr><th>Generated</th><td>{DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)</td></tr>
                        <tr><th>Findings Scope</th><td>Most recent completed assessment. No date filter is applied to the hashed findings.</td></tr>
                        <tr><th>SHA-256 (findings body)</th><td class="mono">{sha256}</td></tr>
                    </table>
                </section>
                """);

            sb.Append(HtmlFoot());
            var html = sb.ToString();
            var key = BundleKey(serverName, "AuditEvidence");
            PendingHtml[key] = html;
            await Task.CompletedTask.ConfigureAwait(false);
            return html;
        }

        // ─────────────────────────────────────────────────────────────────────
        // QuestPDF bundle builders — deterministic, server-side. Replace the
        // crashing browser-print path. Return PDF bytes for the page to save.
        // The watermark decision is made by the caller (which knows server env).
        // ─────────────────────────────────────────────────────────────────────

#if !SQLT_NO_REPORT_EXEC_SUMMARY
        public async Task<byte[]> BuildExecutiveSummaryPdfAsync(string serverName, bool watermark)
        {
            var display = AnonymisedName(serverName);
            var health = await _executiveHealth.GetHealthScoreAsync(serverName).ConfigureAwait(false);
            var top5 = GetMergedFindings(serverName)
                .Where(f => f.ThisServer == null || f.ThisServer.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => SeverityRank(f.Severity))
                .Take(5)
                .Select(ToBundleFinding)
                .ToList();

            var dto = new ExecutiveSummaryBundle
            {
                Meta = BuildMeta(display, "Executive Summary", watermark),
                // C4 (2026-08-05): the DTO carried a bare int, so the PDF drew a 0/100 donut in
                // the red band for a server nothing had measured. ScoreAssessed decides whether
                // the donut is drawn at all, and ScoreBasis carries the same sentence the HTML
                // summary prints, so the two deliverables cannot disagree about one server.
                Score = health.MeasuredScore ?? 0,
                ScoreAssessed = health.MeasuredScore.HasValue,
                ScoreBasis = EstateHealthPolicy.ServerBasis(health),
                ScoreMessage = health.Message ?? string.Empty,
                TopRisks = top5,
            };
            return await Task.Run(() => AssessmentPdf.BuildExecutiveSummaryBundle(dto)).ConfigureAwait(false);
        }
#endif

#if !SQLT_NO_REPORT_DBA_HANDOFF
        public async Task<byte[]> BuildDbaHandoffPdfAsync(string serverName, bool watermark)
        {
            var display = AnonymisedName(serverName);
            var health = _healthCheckService.GetCachedHealth(serverName);
            var allFindings = GetMergedFindings(serverName)
                .Where(f => f.ThisServer == null || f.ThisServer.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => SeverityRank(f.Severity))
                .ToList();
            var failed = allFindings
                .Where(f => f.Status?.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true
                         || f.Severity?.Equals("Error", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            var inventory = new List<(string, string)>();
            if (health != null)
            {
                inventory.Add(("Server Name", health.ServerName ?? display));
                inventory.Add(("Online", health.IsOnline == true ? "Yes" : "No"));
                inventory.Add(("CPU Usage", health.CpuPercent.HasValue ? $"{health.CpuPercent} %" : "—"));
                inventory.Add(("Buffer Pool (MB)", health.BufferPoolMb?.ToString("N0") ?? "—"));
                inventory.Add(("Top Wait Type", health.TopWaitType ?? "—"));
                inventory.Add(("Last Checked", health.LastUpdated?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"));
            }

            var dto = new DbaHandoffBundle
            {
                Meta = BuildMeta(display, "DBA Handoff Package", watermark),
                Inventory = inventory,
                AllFindings = allFindings.Select(ToBundleFinding).ToList(),
                KnownIssues = failed.Select(ToBundleFinding).ToList(),
            };
            return await Task.Run(() => AssessmentPdf.BuildDbaHandoffBundle(dto)).ConfigureAwait(false);
        }
#endif

#if !SQLT_NO_REPORT_EXEC_SUMMARY
        /// <summary>Estate-wide Executive Summary PDF: top risks ranked by servers impacted.</summary>
        public async Task<byte[]> BuildExecutiveSummaryEstatePdfAsync(bool watermark)
        {
            var (rollup, _, serverCount, coverageNote) = GatherEstateRollup();
            var dto = new ExecutiveSummaryBundle
            {
                Meta = BuildMeta($"All Servers ({serverCount})", "Executive Summary — Estate", watermark, coverageNote), // voice-lint:allow an unchanged report title; this line is in the diff only because the coverage-note argument was appended
                // reports-r1-01 (2026-08-27): this path set Score = 0 and left ScoreAssessed at its
                // `true` default, so an estate nothing had measured printed a red "0 / 100 Overall
                // Health Score" on the cover of a client deliverable. Measured before the fix, over
                // two seated and entirely clean servers: "Executive Summary — Estate All Servers (0)
                // ... 0 / 100 Overall Health Score Estate roll-up across 0 servers." No estate health
                // score is computed on this path at all — there is no measurement behind that ring,
                // so the DTO now says so and the builder draws the scope instead of a verdict.
                // EstateScope is set independently of TopRisks: a CLEAN estate has no top risks, and
                // inferring "estate" from the risk rows made an all-clean estate fall through to the
                // single-server donut branch — the exact route the fabricated 0 took.
                Score = 0,
                ScoreAssessed = false,
                EstateScope = true,
                EstateServerCount = serverCount,
                ScoreBasis = "No single health score is computed for an estate roll-up.",
                ScoreMessage = $"Estate roll-up across {serverCount} server{(serverCount == 1 ? "" : "s")}.",
                TopRisks = rollup.Take(5).ToList(),
            };
            return await Task.Run(() => AssessmentPdf.BuildExecutiveSummaryBundle(dto)).ConfigureAwait(false);
        }
#endif

#if !SQLT_NO_REPORT_DBA_HANDOFF
        /// <summary>Estate-wide DBA Handoff PDF: roll-up by check + per-server appendix.</summary>
        public async Task<byte[]> BuildDbaHandoffEstatePdfAsync(bool watermark)
        {
            var (rollup, appendix, serverCount, coverageNote) = GatherEstateRollup();
            var dto = new DbaHandoffBundle
            {
                Meta = BuildMeta($"All Servers ({serverCount})", "DBA Handoff Package — Estate", watermark, coverageNote), // voice-lint:allow an unchanged report title, re-touched only to pass the coverage note through
                Inventory = new List<(string, string)> { ("Scope", $"Estate roll-up — {serverCount} server{(serverCount == 1 ? "" : "s")} scanned") },
                AllFindings = rollup,
                KnownIssues = new List<BundleFinding>(),
                EstateAppendix = appendix,
            };
            return await Task.Run(() => AssessmentPdf.BuildDbaHandoffBundle(dto)).ConfigureAwait(false);
        }
#endif

        // virtual: a test can override this to force a per-server build failure and prove the estate
        // zip reports the dropped server rather than swallowing it (platform-r2-06). No production
        // subclass exists.
        public virtual async Task<byte[]> BuildAuditEvidencePdfAsync(string serverName, bool watermark)
        {
            var display = AnonymisedName(serverName);
            var (allFindings, sha256, scanSha256) = GatherAuditEvidence(serverName, display);

            // reports-r2-09 (2026-08-27): this was `var auditCount = 0;`, assigned only inside the
            // try. With no audit service, or with a read that threw, the compliance PDF printed
            // "Total Audit Events: 0" — a measured-looking zero for a count nobody obtained. Null
            // means NOT MEASURED and renders as a sentence, not a number.
            int? auditCount = null;
            var chainStatus = "N/A (audit service not available)";
            if (_auditLog != null)
            {
                try
                {
                    var entries = _auditLog.GetEntries(DateTime.Now.AddDays(-30), DateTime.Now);
                    auditCount = entries.Count;
                    // Four-state (2026-08-01, round 3), FULL SCOPE (round 4).
                    //
                    // This used to read the STARTUP flags (ChainBroken / ChainProvenanceIndeterminate
                    // / ChainUnverifiable), and startup verification walks only the MOST-RECENT
                    // segment. Measured 2026-08-01 on a read-only, byte-verified copy of the
                    // installed service's production chain: all three flags false, so this artifact
                    // printed a bare "Intact", while VerifyChain on the same launch reported
                    // UNVERIFIABLE with 357 of 363 retained entries unchecked. (One figure, dated,
                    // and it is a snapshot of a growing chain — see the remarks on
                    // DescribeChainForCompliance.) A compliance document is the last place a partial
                    // scan may be reported as a whole one. Reproduced on 791d2cb, so the defect is
                    // pre-existing and it is live.
                    //
                    // A bundle is generated on demand, so it now runs its own FULL verification and
                    // prints a statement that carries its own scope. AuditLogService owns the wording
                    // (DescribeChainForCompliance) so the HTML and the PDF cannot drift apart.
                    //
                    // What that buys, stated as measured and not more (corrected round 5 — it read
                    // "so this artifact and VerifyChain cannot disagree about the same chain", which
                    // measurement falsified twice): the fold is ESCALATE-ONLY, so this artifact may
                    // print a WORSE verdict than a bare VerifyChain over the same bytes, naming the
                    // out-of-band anchor evidence VerifyChain structurally cannot see. It can never
                    // print a better one. That is the guarantee — not agreement, but no unearned
                    // reassurance.
                    chainStatus = _auditLog.DescribeChainForCompliance(
                        "report-bundle:audit-evidence-pdf").Text;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read audit log entries for Audit Evidence PDF");
                    chainStatus = "Could not verify";
                }
            }

            var dto = new AuditEvidenceBundle
            {
                Meta = BuildMeta(display, "Audit Evidence", watermark),
                Findings = allFindings.ToList(),
                AuditEventCount = auditCount ?? 0,
                AuditEventCountAssessed = auditCount.HasValue,
                ChainStatus = chainStatus,
                Sha256 = sha256,
                ScanDataSha256 = scanSha256,
            };
            return await Task.Run(() => AssessmentPdf.BuildAuditEvidenceBundle(dto)).ConfigureAwait(false);
        }

        /// <summary>Distinct servers present in the cached scan (the estate scope). Empty when nothing scanned.</summary>
        public IReadOnlyList<string> ScannedServers() =>
            GetCachedFindings()
                .Select(f => f.ThisServer)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList()!;

        /// <summary>
        /// True when Audit Evidence would actually attest something for this target: at least
        /// one cached Vulnerability Assessment result exists that is either unattributed
        /// (server-level run) or explicitly stamped for <paramref name="serverName"/>. Mirrors
        /// the exact filter <see cref="GatherAuditEvidence"/> uses, so "available" here always
        /// means a real (non-empty) attestation would follow. serverName null = estate mode:
        /// true when <see cref="ScannedServers"/> is non-empty, matching what
        /// <see cref="BuildAuditEvidenceEstateZipAsync"/> actually walks.
        ///
        /// Gates the Audit Evidence export in Pages/ReportBundles.razor (Adrian's ruling,
        /// 2026-07-16, Builder A): never offer the VA-only attestation before a VA has actually
        /// run for the target — an attestation of nothing is worse than none.
        /// </summary>
        public bool HasVaResultsFor(string? serverName)
        {
            if (string.IsNullOrEmpty(serverName))
                return ScannedServers().Count > 0;

            return GetCachedFindings().Any(f => f.ThisServer == null
                || f.ThisServer.Equals(serverName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Audit Evidence for the whole estate: one per-server PDF (each with its own per-server
        /// dual hash), packed into a single zip. Keeps every server's attestation independently
        /// verifiable — an aggregate hash over mixed servers would be weaker evidence. Returns the
        /// zip bytes.
        /// <para>
        /// A server whose per-server PDF build throws is dropped from the zip (logged warning, then
        /// continue). That drop used to be SILENT: the CLI printed "[audit-evidence] wrote &lt;path&gt;"
        /// and exited 0 over a zip missing servers the scan actually covered (platform-r2-06). Pass
        /// <paramref name="skippedServers"/> to receive the dropped names so the caller can report a
        /// partial attestation and fail; leave it null to keep the historical behaviour.
        /// </para>
        /// </summary>
        public async Task<byte[]> BuildAuditEvidenceEstateZipAsync(
            bool watermarkAll, List<string>? skippedServers = null)
        {
            var servers = ScannedServers();
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var server in servers)
                {
                    byte[] pdf;
                    try
                    {
                        pdf = await BuildAuditEvidencePdfAsync(server, watermarkAll).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Audit Evidence batch: skipping server {Server} after error", server);
                        skippedServers?.Add(server);
                        continue;
                    }

                    var safe = server.Replace("\\", "_").Replace("/", "_").Replace(":", "_");
                    var entry = zip.CreateEntry($"AuditEvidence_{safe}.pdf", CompressionLevel.Optimal);
                    using var es = entry.Open();
                    await es.WriteAsync(pdf).ConfigureAwait(false);
                }
            }
            return ms.ToArray();
        }

        // ── Risk Register entry points ───────────────────────────────────────
        // serverName == null ⇒ estate (all scanned servers). acknowledgement ⇒
        // render the signature instrument. formalTone ⇒ ISO/NIST vs plain wording.

        public Task<string> PrepareRiskRegisterHtmlAsync(string? serverName, bool acknowledgement, bool formalTone, string preparedBy)
        {
            var b = GatherRiskRegister(serverName);
            b.Acknowledgement = acknowledgement;
            b.FormalTone = formalTone;
            b.PreparedBy = preparedBy ?? string.Empty;
            var display = serverName == null ? "All Servers" : AnonymisedName(serverName);
            return Task.FromResult(RenderRiskRegisterHtml(b, display));
        }

        public async Task<byte[]> BuildRiskRegisterPdfAsync(string? serverName, bool acknowledgement, bool formalTone, string preparedBy, bool watermark)
        {
            var b = GatherRiskRegister(serverName);
            b.Acknowledgement = acknowledgement;
            b.FormalTone = formalTone;
            b.PreparedBy = preparedBy ?? string.Empty;
            var display = serverName == null ? "All Servers" : AnonymisedName(serverName);
            b.Meta = BuildMeta(display, acknowledgement ? "Risk Acknowledgement" : "Risk Register", watermark);
            return await Task.Run(() => AssessmentPdf.BuildRiskRegisterBundle(b)).ConfigureAwait(false);
        }

        /// <summary>The accountability-transfer paragraph — formal (ISO/NIST) or plain ("cover the DBA").</summary>
        internal static string AcknowledgementStatement(bool formalTone) => formalTone
            ? "The undersigned acknowledges the risks recorded in this register. By approving remediation, the organization authorises the work and prioritisation required to address them. By declining or deferring remediation, the undersigned formally accepts these risks on behalf of the organization. Responsibility for any incident arising from an accepted risk rests with the organization, not with the preparer of this register."
            : "I confirm that the issues listed above have been brought to management's attention. Approve to authorise the work and the time needed to fix them. If approval is declined or deferred, management accepts the listed risks — including any consequences of the items marked Critical — and responsibility for those outcomes does not rest with the person who raised them.";

        private string RenderRiskRegisterHtml(RiskRegisterBundle b, string display)
        {
            var estate = b.Rows.Any(r => r.ServersTotal > 0);
            var title = b.Acknowledgement ? "Risk Acknowledgement" : "Risk Register";
            var sb = new StringBuilder();
            sb.Append(HtmlHead(title));
            sb.Append($"""
                <div class="rb-header">
                    <div class="rb-tag">{title} — for management review &amp; sign-off</div>
                    <h1>{EscapeHtml(display)}</h1>
                    <div class="rb-meta">Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
                </div>
                """);

            // Summary counts
            sb.Append($"""
                <section>
                    <h2>Summary</h2>
                    <table class="rb-table rb-kv">
                        <tr><th>Critical risks</th><td>{b.CriticalCount}</td></tr>
                        <tr><th>High risks</th><td>{b.HighCount}</td></tr>
                        <tr><th>Other tracked risks</th><td>{b.OtherCount}</td></tr>
                        <tr><th>Total</th><td>{b.Rows.Count}</td></tr>
                    </table>
                </section>
                """);

            sb.Append("<section><h2>Risk Register</h2>");
            if (b.Rows.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No outstanding bad-state risks. Run the check suite (Audit Assessment) first, or the estate is clean.</p>");
            }
            else
            {
                var impactedHead = estate ? "<th>Servers</th>" : "";
                sb.Append($"<table class=\"rb-table\"><thead><tr><th>ID</th><th>Severity</th><th>Risk</th><th>Category</th><th>Owner</th><th>Review by</th>{impactedHead}<th>Business Impact</th></tr></thead><tbody>");
                foreach (var r in b.Rows)
                {
                    var impactedCell = estate ? $"<td>{EscapeHtml(r.ServersImpactedLabel)}</td>" : "";
                    var reviewBy = r.ReviewByUtc.HasValue ? r.ReviewByUtc.Value.ToString("yyyy-MM-dd") : "—";
                    sb.Append($"<tr><td>{EscapeHtml(r.Id)}</td><td class=\"sev sev-{r.Severity?.ToLowerInvariant()}\">{EscapeHtml(r.Severity)}</td><td>{EscapeHtml(r.Risk)}</td><td>{EscapeHtml(r.Category)}</td><td>{EscapeHtml(r.Owner)}</td><td>{reviewBy}</td>{impactedCell}<td>{EscapeHtml(r.BusinessImpact)}</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");

            if (b.Acknowledgement)
            {
                sb.Append($"""
                    <section class="rb-ack">
                        <h2>Risk Acknowledgement &amp; Approval</h2>
                        <p class="rb-ack-statement">{EscapeHtml(AcknowledgementStatement(b.FormalTone))}</p>
                        <table class="rb-table rb-kv rb-ack-block">
                            <tr><th>Prepared by</th><td>{(string.IsNullOrEmpty(b.PreparedBy) ? "______________________________" : EscapeHtml(b.PreparedBy))}</td></tr>
                            <tr><th>Decision</th><td>☐ Remediation approved &nbsp;&nbsp; ☐ Risk accepted (deferred)</td></tr>
                            <tr><th>Name &amp; title</th><td>______________________________</td></tr>
                            <tr><th>Signature</th><td>______________________________</td></tr>
                            <tr><th>Date</th><td>______________________________</td></tr>
                        </table>
                    </section>
                    """);
            }

            sb.Append(HtmlFoot());
            return sb.ToString();
        }

        /// <summary><paramref name="coverageNote"/> is the scope qualifier the bundle title band
        /// prints directly under the subtitle — empty for every single-server bundle, and non-empty
        /// on an estate roll-up whose seat filter left servers out (see
        /// <see cref="ScannedEstateSeats"/>). A cover that states a scanned-server count states what
        /// the count excludes in the same place, or the count reads as the whole estate.</summary>
        private AssessmentMeta BuildMeta(string display, string title, bool watermark, string coverageNote = "")
        {
            var nowUtc = DateTime.UtcNow;
            var tz = TimeZoneInfo.Local.StandardName;
            var runId = Guid.NewGuid().ToString("N")[..8];
            return new AssessmentMeta
            {
                Title = title,
                Company = _userSettings.GetReportCompanyName(),
                Subtitle = display,
                Engine = "SQLTriage diagnostic bundle",
                GeneratedUtc = nowUtc.ToString("yyyy-MM-ddTHH:mmZ"),
                TimezoneId = tz,
                RunId = runId,
                CoverageNote = coverageNote,
                ColorBlind = _userSettings.GetColorBlindMode(),
                Watermark = watermark,
                FooterMeta = $"SQLTriage — {title} — {display} — {nowUtc:yyyy-MM-ddTHH:mmZ} ({tz}) — Run {runId}",
            };
        }

        // Memoised id→check index (canonical Id + every LegacyId). Rebuilt only when
        // the underlying corpus list is swapped, so enrichment is O(1) per finding
        // instead of an O(N) scan of every check per finding.
        private IReadOnlyList<SqlCheck>? _indexedChecks;
        private Dictionary<string, SqlCheck>? _checkIndex;

        private Dictionary<string, SqlCheck> CheckIndex()
        {
            // GetAllChecks() returns the backing list directly (Checks wraps it in a
            // fresh AsReadOnly each call), so its reference is a stable per-load key.
            var checks = _checkRepo.GetAllChecks();
            if (!ReferenceEquals(checks, _indexedChecks) || _checkIndex == null)
            {
                var index = new Dictionary<string, SqlCheck>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in checks)
                {
                    if (!string.IsNullOrEmpty(c.Id))
                        index[c.Id] = c;
                    foreach (var legacy in c.LegacyIds)
                    {
                        if (!string.IsNullOrEmpty(legacy) && !index.ContainsKey(legacy))
                            index[legacy] = c;
                    }
                }
                _checkIndex = index;
                _indexedChecks = checks;
            }
            return _checkIndex;
        }

        private BundleFinding ToBundleFinding(AssessmentResult f)
        {
            // Find matching corpus check by ID or Legacy ID via the memoised index.
            SqlCheck? corpusCheck = null;
            if (f.CheckId != null)
                CheckIndex().TryGetValue(f.CheckId, out corpusCheck);

            var enrichedCategory = corpusCheck?.Category ?? f.Category ?? string.Empty;

            // reports-r2-02 (2026-08-27): a check that could not RUN carries
            // Status = ComplianceScoreService.UnassessedStatus, and this method used to hand it the
            // same treatment as a genuine finding — the corpus check DESCRIPTION in place of the
            // actual error text, plus the corpus business-impact prose. Measured before the fix, the
            // top row of a client-facing "Top 5 Risks": "E1 Critical TDE enabled on all databases
            // Security Error: Login failed for user 'x'." A check that never ran has no business
            // impact and no described symptom, so it keeps its OWN message and states its status.
            var unassessed = IsUnassessed(f);

            return new BundleFinding
            {
                Id = f.CheckId ?? string.Empty,
                Severity = f.Severity ?? string.Empty,
                Name = f.DisplayName ?? string.Empty,
                Category = enrichedCategory,
                Message = unassessed
                    ? (f.Message ?? string.Empty)
                    : (corpusCheck?.Description ?? f.Message ?? string.Empty),
                Remediation = corpusCheck?.DetailedRemediation ?? corpusCheck?.RecommendedAction ?? f.Remediation ?? string.Empty,
                // Use the canonical corpus Id (when matched) so FrameworkMappings — keyed on SqlCheck.Id — resolves even when f.CheckId is a legacy id.
                Framework = MapFramework(enrichedCategory, corpusCheck?.Id ?? f.CheckId),
                Status = f.Status ?? string.Empty,
                Source = string.IsNullOrEmpty(f.Source) ? "Microsoft VA" : f.Source,
                // Client-facing voice (corpus '## Business Impact'); business-audience bundles
                // render this instead of the Intent-derived Message (which can carry oracle notes).
                // Never for an unassessed row: the impact prose describes a control BREACH, and this
                // check made no assertion about the control at all (reports-r2-02).
                BusinessImpact = unassessed ? string.Empty : (corpusCheck?.BusinessImpact ?? string.Empty),
            };
        }

        /// <summary>
        /// Records a successful generation (called by the caller after the HTML file write or the
        /// PDF byte write succeeds — see the class doc's two output paths).
        /// </summary>
        public void RecordSuccess(string serverName, string bundleType, string outputPath)
        {
            var key = BundleKey(serverName, bundleType);
            LastGenerated[key] = DateTime.Now;
            _auditLog?.LogReportBundle(bundleType, AnonymisedName(serverName), true, outputPath);
            _logger.LogInformation("Report bundle '{BundleType}' saved to {Path}", bundleType, outputPath);
        }

        /// <summary>
        /// Records a failed generation.
        /// </summary>
        public void RecordFailure(string serverName, string bundleType, string error)
        {
            _auditLog?.LogReportBundle(bundleType, AnonymisedName(serverName), false, null, error);
            _logger.LogWarning("Report bundle '{BundleType}' failed for '{Server}': {Error}", bundleType, serverName, error);
        }

        /// <summary>
        /// Returns the last-generated timestamp for a server+bundleType pair, or null.
        /// </summary>
        public DateTime? GetLastGenerated(string serverName, string bundleType)
        {
            return LastGenerated.TryGetValue(BundleKey(serverName, bundleType), out var dt) ? dt : null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private string AnonymisedName(string serverName)
            => _userSettings.GetAnonymiseServerNames() ? "[server]" : serverName;

        private static string BundleKey(string serverName, string bundleType)
            => $"{serverName}|{bundleType}";

        /// <summary>Lightweight framework tag from category — placeholder hook for Compliance Framework feature.</summary>
        private string MapFramework(string? category, string? checkId)
        {
            // Try to resolve precise framework mappings from the corpus index
            if (!string.IsNullOrEmpty(checkId) && _checkRepo.FrameworkMappings.TryGetValue(checkId, out var mappings) && mappings.Any())
            {
                return string.Join(" / ", mappings.Select(m => m.Framework).Distinct());
            }

            // Fallback heuristics
            return category?.ToUpperInvariant() switch
            {
                "SECURITY" => "CIS / NIST AC",
                "CONFIGURATION" => "CIS / NIST CM",
                "PERFORMANCE" => "—",
                "AVAILABILITY" => "SOC2 A1",
                "BESTPRACTICES" or "BEST PRACTICES" => "CIS",
                _ => "—"
            };
        }

        /// <summary>Client-facing wording for a finding's Status (reports-r2-02). "Unassessed" is the
        /// corpus token; a client reading a deliverable is not holding that vocabulary, so the column
        /// says what actually happened. Same wording as the PDF's own column in
        /// <see cref="AssessmentPdf"/> — the HTML twin and the PDF must not describe one row two
        /// different ways.</summary>
        private static string StatusLabel(string? status)
            => string.IsNullOrWhiteSpace(status) ? "—" // voice-lint:allow the established empty-cell glyph in these tables
             : IsUnassessedStatus(status)
                ? "Could not run"
                : status!;

        /// <summary>"This row made no assertion about this server." One predicate, because the row's
        /// wording, its impact count and its representative-row choice all key on it and a copy that
        /// drifted would put a number beside "Could not run" again (reports-r2-02, second round).</summary>
        private static bool IsUnassessedStatus(string? status) =>
            string.Equals(status, ComplianceScoreService.UnassessedStatus, StringComparison.OrdinalIgnoreCase);

        private static bool IsUnassessed(AssessmentResult f) => IsUnassessedStatus(f.Status);

        private static int SeverityRank(string? severity) => severity?.ToLowerInvariant() switch
        {
            "error" or "critical" or "high" => 3,
            "warning" or "medium" => 2,
            "information" or "info" or "low" => 1,
            _ => 0
        };

        /// <summary>Server label used when a finding carries no ThisServer attribution.</summary>
        private const string UnattributedServer = "(unattributed)";

        /// <summary>The estate scope qualifier for an HTML twin: the same sentence the PDF's title
        /// band prints, in the same place relative to the count it qualifies. Empty note ⇒ empty
        /// markup, so an unaffected report is unchanged.</summary>
        private static string CoverageHtml(string coverageNote)
            => string.IsNullOrWhiteSpace(coverageNote)
                ? string.Empty
                : $"<div class=\"rb-coverage\">{EscapeHtml(coverageNote)}</div>";

        /// <summary>
        /// The estate SCOPE: every server the report covers. This is the denominator behind
        /// "N servers scanned" on both estate covers and behind every "N of M servers impacted"
        /// cell.
        ///
        /// <para>reports-r2-01 (2026-08-27): it used to be derived from the FINDINGS
        /// (<see cref="GetMergedFindings"/> keeps only failing rows), so a server that was scanned
        /// and came back clean was invisible in the count and a wholly clean estate reported
        /// "0 servers scanned". Measured before the fix, two seated servers with one clean:
        /// "All Servers (1) ... Estate Overview 1 server scanned"; with both clean, "All Servers (0)".
        /// The scanned set is the honest denominator, and it was already being read two lines away
        /// and discarded.</para>
        ///
        /// <para>The finding labels are UNIONed in rather than replaced: a finding with no server
        /// attribution lands in the "(unattributed)" bucket, which is a real column in the roll-up,
        /// so dropping it would let an impacted count exceed the total it is quoted against.</para>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests) so the denominator is measurable on
        /// its own, without a live CheckExecutionService and its shared on-disk result store.</para>
        /// </summary>
        internal static List<string> EstateServerScope(
            IEnumerable<string>? scannedServers,
            IEnumerable<AssessmentResult> findings)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (scannedServers != null)
            {
                foreach (var s in scannedServers)
                    if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
            }
            foreach (var f in findings)
                set.Add(string.IsNullOrEmpty(f.ThisServer) ? UnattributedServer : f.ThisServer!);
            return set.ToList();
        }

        /// <summary>
        /// The seat-filtered scanned set AND what the seat filter left out, taken as ONE projection.
        /// Null when it could not be read — the caller then falls back to the finding-derived labels
        /// alone, which is a FLOOR on the estate size, never an inflation.
        ///
        /// <para>2026-08-27 fix round. This called <c>GetServersWithResults()</c>, whose own contract
        /// (CheckExecutionService) reads: "Callers that list servers must surface GetExcludedServers()
        /// so the omission is stated, never silent … If you add a caller, wire the banner too." This
        /// caller did not, and the estate cover then printed "N servers scanned" over the seated
        /// subset with nothing naming the omission — a scanned-set claim, on a client deliverable,
        /// made over a filtered set. <c>GetServerSeatFilter()</c> is the single-projection form the
        /// contract prefers, so the count and the banner cannot disagree.</para>
        /// </summary>
        private SQLTriage.Data.Services.Licensing.SeatFilter? ScannedEstateSeats()
        {
            if (_checkExecution == null) return null;
            try
            {
                return _checkExecution.GetServerSeatFilter();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the scanned server set for the estate roll-up");
                return null;
            }
        }

        /// <summary>
        /// Aggregates the cached findings across the whole scanned estate for an All-Servers
        /// report. Rather than one row per (server × finding) — which bloats on a large estate —
        /// findings are grouped by CheckId into one roll-up row carrying a distinct-server count
        /// ("Servers Impacted: N of M"). A per-server appendix lists each server's impacted checks
        /// once, so full detail is preserved without inflating the hot table. Null-attribution
        /// findings fall into a single "(unattributed)" bucket — never fanned across all servers.
        /// </summary>
        private (List<BundleFinding> Rollup, List<EstateServerEntry> Appendix, int ServerCount, string CoverageNote) GatherEstateRollup()
        {
            var raw = GetMergedFindings(null);

            // Distinct scanned servers = the denominator for "N of M".
            var seats = ScannedEstateSeats();
            var servers = EstateServerScope(seats?.Seated, raw);
            var serverCount = servers.Count;

            // The other half of that projection: servers with results that the licence does not
            // cover. SeatFilter.ExclusionNotice is the repo's own banner wording ("N instance(s)
            // excluded: not covered by your licence (…)"), rendered verbatim beside the count it
            // qualifies. Empty on an unlimited/legacy licence, so an unaffected estate report is
            // byte-identical to what it was.
            var coverageNote = seats?.ExclusionNotice ?? string.Empty;

            // Group by check (CheckId, falling back to DisplayName for unkeyed legacy rows).
            var rollup = new List<BundleFinding>();
            foreach (var grp in raw.GroupBy(f => !string.IsNullOrEmpty(f.CheckId) ? f.CheckId! : f.DisplayName ?? string.Empty,
                                            StringComparer.OrdinalIgnoreCase))
            {
                // ── One row, one story (reports-r2-02, second round 2026-08-27) ───────────────
                //
                // A group can hold both kinds of row for one check: it genuinely FAILED on one
                // server and could not RUN on another. The row that represents the group therefore
                // decides what the whole row says, and the count beside it must agree.
                //
                // Assessed rows sort first, THEN by severity. So the row speaks for a real finding
                // whenever there is one, and reads "could not run" only when nothing ran anywhere.
                // Before this, the representative was chosen on severity alone, so a Critical
                // errored instance could speak for a check that really did fail elsewhere.
                var assessed = grp.Where(f => !IsUnassessed(f)).ToList();
                var rep = (assessed.Count > 0 ? assessed : grp.ToList())
                    .OrderByDescending(f => SeverityRank(f.Severity)).First();
                var bf = ToBundleFinding(rep);

                // The count is of servers where the check RAN and failed. A server whose run of it
                // errored has told us nothing about whether it is impacted, so counting it stated a
                // measurement nobody took — the gate found the estate cover printing "Could not run"
                // and "1 of 1" in the same row. Empty ⇒ the cell prints no number at all.
                bf.ServersImpacted = assessed
                    .Select(f => string.IsNullOrEmpty(f.ThisServer) ? UnattributedServer : f.ThisServer!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                bf.ServersImpactedAssessed = assessed.Count > 0;
                bf.ServersTotal = serverCount;
                rollup.Add(bf);
            }

            rollup = rollup
                .OrderByDescending(f => SeverityRank(f.Severity))
                .ThenByDescending(f => f.ServersImpacted)
                .ToList();

            // Appendix: one entry per server with its impacted check ids (failed/error only —
            // the "what to remediate here" list; passing checks would bloat without value).
            var appendix = raw
                .Where(f => f.Status?.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true
                         || f.Severity?.Equals("Error", StringComparison.OrdinalIgnoreCase) == true)
                .GroupBy(f => string.IsNullOrEmpty(f.ThisServer) ? UnattributedServer : f.ThisServer!,
                         StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new EstateServerEntry
                {
                    Server = g.Key,
                    FindingCount = g.Count(),
                    CheckIds = g.Select(f => !string.IsNullOrEmpty(f.CheckId) ? f.CheckId! : f.DisplayName ?? string.Empty)
                                .Where(s => !string.IsNullOrEmpty(s))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                                .ToList(),
                })
                .ToList();

            return (rollup, appendix, serverCount, coverageNote);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Risk Register / Acknowledgement — bad=1 failing checks framed for a
        // manager/exec, with the accountability-transfer instrument.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the risk register from cached findings: only Failed/Error results whose corpus
        /// check is flagged bad=1 (IsBad). Each becomes a register row with a plain-English
        /// business-impact line. Estate mode (serverName == null) rolls up by check with a
        /// servers-impacted count; otherwise it's a single-server register.
        /// </summary>
        private RiskRegisterBundle GatherRiskRegister(string? serverName)
        {
            var estate = string.IsNullOrEmpty(serverName);

            // Corpus-only source (honesty ruling 2026-07-16, Builder A) — the Risk Register
            // reflects the full corpus diagnostic picture; VA no longer feeds it.
            var merged = GetMergedFindings(serverName);

            var failing = merged
                .Where(f => f.ThisServer == null || serverName == null
                            || f.ThisServer.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .Where(f => f.Status?.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true
                         || f.Severity?.Equals("Error", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            // Prefer the bad=1 (IsBad) set — the fundable-risk subset per the original sheet.
            // But IsBad is only populated when the corpus carries it; in builds where it's
            // universally false, gating on it would empty the register. So: if ANY failing
            // check is flagged bad, keep only those; otherwise every Failed/Error finding is a
            // risk (a failing check IS a risk — IsBad is a refinement, not the gate).
            var index = CheckIndex();
            SqlCheck? Lookup(AssessmentResult f) =>
                f.CheckId != null && index.TryGetValue(f.CheckId, out var c) ? c : null;
            if (failing.Any(f => Lookup(f)?.IsBad == true))
                failing = failing.Where(f => Lookup(f)?.IsBad == true).ToList();

            int serverTotal = merged
                .Select(f => string.IsNullOrEmpty(f.ThisServer) ? UnattributedServer : f.ThisServer!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();

            List<RiskRegisterRow> rows;
            if (estate)
            {
                rows = failing
                    .GroupBy(f => !string.IsNullOrEmpty(f.CheckId) ? f.CheckId! : f.DisplayName ?? string.Empty,
                             StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        // Same rule as the estate roll-up: the row speaks for a real finding when
                        // there is one, and the impacted count counts only servers where the check
                        // ran. An errored instance has told us nothing about that server. Latent on
                        // today's corpus for the reason BuildRiskRow states below.
                        var assessed = g.Where(f => !IsUnassessed(f)).ToList();
                        var rep = (assessed.Count > 0 ? assessed : g.ToList())
                            .OrderByDescending(f => SeverityRank(f.Severity)).First();
                        return BuildRiskRow(rep, Lookup(rep),
                            impacted: assessed.Select(f => string.IsNullOrEmpty(f.ThisServer) ? UnattributedServer : f.ThisServer!)
                                              .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                            total: serverTotal);
                    })
                    .ToList();
            }
            else
            {
                rows = failing.Select(f => BuildRiskRow(f, Lookup(f), 0, 0)).ToList();
            }

            rows = rows.OrderByDescending(r => SeverityRank(r.Severity)).ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList();

            return new RiskRegisterBundle
            {
                Rows = rows,
                CriticalCount = rows.Count(r => SeverityRank(r.Severity) == 3 && IsCritical(r.Severity)),
                HighCount = rows.Count(r => SeverityRank(r.Severity) == 3 && !IsCritical(r.Severity)),
                OtherCount = rows.Count(r => SeverityRank(r.Severity) < 3),
            };
        }

        /// <summary>
        /// Public accessor for the Risk Register rows, used by the in-app owner-assignment
        /// editor. serverName null = estate roll-up.
        /// </summary>
        public RiskRegisterBundle GetRiskRegister(string? serverName) => GatherRiskRegister(serverName);

        /// <summary>
        /// Persists a per-finding owner / review-by assignment (Risk Register editor). The
        /// assignment is keyed by (server, checkId) and overrides the default-derived owner
        /// in every subsequent report build. server null/empty = estate-wide default.
        /// </summary>
        public void AssignOwner(string? server, string checkId, string? owner, DateTime? reviewByUtc, string assignedBy)
            => _ownerStore.Set(server, checkId, owner, reviewByUtc, assignedBy);

        private static bool IsCritical(string? sev) =>
            sev?.ToLowerInvariant() is "error" or "critical";

        /// <summary>Default review cadence for register rows — mirrors Rbac:AccessReviewDays (90d). Review-by = report date + this.</summary>
        private const int ReviewPeriodDays = 90;

        private RiskRegisterRow BuildRiskRow(AssessmentResult f, SqlCheck? corpus, int impacted, int total)
        {
            // Default owner = the configured report operator (falls back to OS user);
            // default review-by = report date + cadence. A persisted per-finding
            // assignment (set in the Risk Register UI) overrides both and survives
            // across scans because it is keyed by (server, checkId).
            var owner = _userSettings.GetReportOperatorName();
            if (string.IsNullOrWhiteSpace(owner)) owner = Environment.UserName;
            var reviewBy = DateTime.UtcNow.Date.AddDays(ReviewPeriodDays);

            var assignment = _ownerStore.Get(f.ThisServer, f.CheckId ?? string.Empty);
            if (assignment != null)
            {
                if (!string.IsNullOrWhiteSpace(assignment.Owner)) owner = assignment.Owner;
                if (assignment.ReviewByUtc != default) reviewBy = assignment.ReviewByUtc;
            }

            return new()
            {
                Id = f.CheckId ?? string.Empty,
                Severity = f.Severity ?? string.Empty,
                Risk = corpus?.Name ?? f.DisplayName ?? f.CheckId ?? "Unnamed risk",
                Category = corpus?.Category ?? f.Category ?? string.Empty,
                BusinessImpact = BusinessImpactFor(corpus, f),
                Remediation = corpus?.DetailedRemediation ?? corpus?.RecommendedAction ?? f.Remediation ?? string.Empty,
                Owner = owner,
                ReviewByUtc = reviewBy,
                ServersImpacted = impacted,
                ServersTotal = total,
                // reports-r2-02, second round (2026-08-27). LATENT, not live, and said plainly:
                // the register admits a row on "Status is Failed OR Severity is Error", and a check
                // that ERRORED carries Status = Unassessed while keeping its declared severity — so
                // the Severity arm would let a check that never ran into a client's risk register
                // with an impacted-server count beside it. Measured on the current corpus: all 582
                // check sources carry a severity line and every one is Low/Info/High/Medium/
                // Critical, so nothing reaches this arm today. It is closed because the arm exists,
                // and because the same shape one table over is what the gate caught live.
                ServersImpactedAssessed = !IsUnassessed(f),
            };
        }

        /// <summary>
        /// Plain-English business impact for a risk = corpus description + a severity-keyed
        /// consequence clause. FLAVOUR SEAM: today this is one neutral voice; the consequence
        /// clause and description source are the single place a per-segment "flavour" (US/UK
        /// compliance vs MSP-deliverable vs ops-continuity) will later be selected. Keep all
        /// audience-framing here; never flavour the underlying finding.
        /// </summary>
        private string BusinessImpactFor(SqlCheck? corpus, AssessmentResult f)
        {
            // Prefer the corpus '## Business Impact' (client voice). Fall back to the Intent-derived
            // Description only when a check has no business-impact prose — never the other way round,
            // so the Intent's oracle-derivation notes don't reach a business-facing register.
            var basis = corpus?.BusinessImpact;
            if (string.IsNullOrWhiteSpace(basis)) basis = corpus?.Description;
            if (string.IsNullOrWhiteSpace(basis)) basis = f.Description;
            if (string.IsNullOrWhiteSpace(basis)) basis = f.Message;
            basis = (basis ?? string.Empty).Trim();

            var consequence = (f.Severity?.ToLowerInvariant()) switch
            {
                "error" or "critical" => "If unaddressed, this carries a high likelihood of data loss, outage, or audit failure.",
                "high"                => "If unaddressed, this materially raises the risk of a security or availability incident.",
                "warning" or "medium" => "Left unattended, this degrades reliability or compliance posture over time.",
                _                     => "A minor risk that should be tracked and scheduled.",
            };

            return string.IsNullOrEmpty(basis) ? consequence : $"{basis} {consequence}";
        }

        private List<AssessmentResult> GetCachedFindings()
        {
            try
            {
                // VulnerabilityAssessmentStateService holds the last VA scan results.
                // Returns empty list when no scan has been run yet — page instructs users accordingly.
                return _vaState.HasRun ? new List<AssessmentResult>(_vaState.Results) : new List<AssessmentResult>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not retrieve cached VA findings");
                return new List<AssessmentResult>();
            }
        }

        /// <summary>
        /// Adapts a corpus <see cref="CheckResult"/> to the bundle's <see cref="AssessmentResult"/>
        /// shape, tagged Source="Corpus". Status comes from
        /// <see cref="ComplianceScoreService.StatusFor"/> — the single mapping shared with the two
        /// razor adapters, which maps every non-assertion (WARN/SKIP/INFO) to
        /// <see cref="ComplianceScoreService.UnassessedStatus"/> and only a scorable result to
        /// Passed/Failed.
        ///
        /// These rows feed ComplianceScoreService, so a mislabel here becomes a compliance
        /// percentage a client reads. Ruling #4 (2026-07-20) fixed the WARN arm; the 2026-07-20
        /// sweep found SKIP and INFO inflating the same number the same way and moved the whole
        /// mapping behind one function.
        /// </summary>
        private static AssessmentResult CorpusToAssessment(CheckResult r) => new()
        {
            CheckId = r.CheckId ?? string.Empty,
            DisplayName = r.CheckName ?? string.Empty,
            Message = r.Message ?? string.Empty,
            Severity = r.Severity ?? string.Empty,
            Category = r.Category ?? string.Empty,
            Description = r.Description ?? string.Empty,
            Remediation = r.RecommendedAction ?? string.Empty,
            Status = ComplianceScoreService.StatusFor(r),
            ThisServer = string.IsNullOrEmpty(r.InstanceName) ? null : r.InstanceName,
            Source = "Corpus",
        };

        /// <summary>
        /// Returns the operational finding set for a report: FAILING corpus check results only.
        /// serverName null = estate roll-up (every server with cached corpus results).
        ///
        /// Honesty ruling (2026-07-16, Builder A): this used to UNION the MS VA findings in too
        /// (preferring corpus on a (server, checkId) collision). That union is gone — Executive
        /// Summary, DBA Handoff, and Risk Register (the three bundles that call this) are now
        /// corpus-only, same as Executive Health. No corpus results ⇒ an honest empty finding
        /// set, never a silent VA stand-in.
        ///
        /// NOTE: Audit Evidence deliberately does NOT use this — it stays a single-source MS VA
        /// attestation with its own verifiable hash (<see cref="GatherAuditEvidence"/> /
        /// <see cref="GetCachedFindings"/>, untouched by this ruling).
        ///
        /// Only FAILING corpus results are included: a "finding" in these bundles is a problem to
        /// act on, and this avoids bloating the report with hundreds of passing checks. The
        /// SQLite-hydrate fidelity gap (IsBad/effort dropped) does not bite here — the Risk
        /// Register backfills IsBad from the corpus index.
        /// </summary>
        private List<AssessmentResult> GetMergedFindings(string? serverName)
        {
            if (_checkExecution == null) return new List<AssessmentResult>();

            try
            {
                var servers = !string.IsNullOrEmpty(serverName)
                    ? new List<string> { serverName }
                    : _checkExecution.GetServersWithResults()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                var corpus = new List<AssessmentResult>();
                foreach (var s in servers)
                {
                    var results = _checkExecution.GetResults(s, maxCount: 2000);
                    corpus.AddRange(results
                        .Where(r => !r.Passed && !r.IsCorrupted)
                        .Select(CorpusToAssessment));
                }

                return corpus;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not gather corpus findings for report bundle");
                return new List<AssessmentResult>();
            }
        }

        private static string ComputeSha256(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string EscapeHtml(string? s)
            => string.IsNullOrEmpty(s) ? string.Empty
               : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// <summary>
        /// Flatten a corpus Markdown body to plain text for the HTML bundles.
        /// Mirrors AssessmentPdf.PlainClamp minus the clamp -- the HTML deliverable is not truncated.
        /// </summary>
        private static string PlainText(string? s) => AssessmentPdf.MarkdownToPlain(s);

        // $$ raw interpolated string: interpolation delimiter is {{ }} so CSS
        // uses natural SINGLE braces. (A single-$ literal hit CS9006 on the
        // nested @media rule because {{ body {{ ... }} }} exceeds the brace
        // depth a single-$ raw string can disambiguate.)
        private static string HtmlHead(string title) => $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8"/>
            <title>{{EscapeHtml(title)}} — SQLTriage</title>
            <style>
            *, *::before, *::after { box-sizing: border-box; }
            body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 11px; color: #0E1714; margin: 0; padding: 16px 24px; background: #F6F8F7; }
            .rb-header { border-bottom: 2px solid #1FA85E; padding-bottom: 12px; margin-bottom: 20px; }
            .rb-tag { font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .08em; color: #5A6A63; margin-bottom: 4px; }
            h1 { margin: 0 0 4px; font-size: 20px; font-weight: 700; }
            h2 { font-size: 13px; font-weight: 700; margin: 0 0 8px; color: #15703F; border-bottom: 1px solid #E2E8E5; padding-bottom: 4px; }
            .rb-meta { font-size: 10px; color: #5A6A63; }
            /* Scope qualifier: what the header's server count leaves out. Amber, not muted grey —
               it qualifies the count directly above it and must not read as boilerplate. */
            .rb-coverage { font-size: 10px; font-style: italic; color: #8A5A00; margin: 2px 0 4px; }
            .rb-sha { font-size: 9px; font-family: monospace; color: #5A6A63; margin-top: 4px; word-break: break-all; }
            section { margin-bottom: 20px; page-break-inside: avoid; }
            .rb-table { border-collapse: collapse; width: 100%; margin-bottom: 8px; background: #FFFFFF; }
            .rb-table th, .rb-table td { border: 1px solid #E2E8E5; padding: 4px 8px; text-align: left; vertical-align: top; }
            .rb-table thead th { background: #E2E8E5; font-weight: 700; }
            .rb-table tr:nth-child(even) { background: #F6F8F7; }
            .rb-kv th { width: 200px; font-weight: 700; background: #E2E8E5; }
            .sev-error, .sev-critical, .sev-high { color: var(--red, #c00); font-weight: 700; }
            .sev-warning, .sev-medium { color: var(--orange, #c60); font-weight: 600; }
            .sev-information, .sev-info, .sev-low { color: var(--green, #060); }
            .score-block { display: inline-block; border: 2px solid #E2E8E5; border-radius: 8px; padding: 12px 24px; margin: 8px 0; }
            .score-good { border-color: var(--green, #4caf50); }
            .score-warn { border-color: var(--orange, #ff9800); }
            .score-bad { border-color: var(--red, #f44336); }
            /* No measurement stood behind the number: neutral, never a band. */
            .score-unknown { border-color: #B9C2BE; border-style: dashed; }
            .score-number { font-size: 32px; font-weight: 900; }
            .score-label { font-size: 16px; color: #5A6A63; }
            .score-msg { font-size: 11px; color: #5A6A63; margin-top: 4px; }
            .rb-empty { color: #5A6A63; font-style: italic; }
            .rb-placeholder { color: #5A6A63; font-style: italic; background: #F6F8F7; padding: 12px; border-radius: 4px; border: 1px dashed #E2E8E5; }
            /* The chain verdict's class comes from ComplianceChainStatement.Label lower-cased, so
               there is one per AuditLogService.ChainVerificationStatus plus -unknown. Measured on a
               generated bundle 2026-08-01: only -intact and -broken existed, so the one CLEAN
               verdict rendered green and bold while the two verdicts the runbook says must not be
               closed without independent evidence rendered as plain body text — the document styled
               reassurance and left doubt unmarked. Those two are deliberately at least as loud as
               -broken: amber-on-tint for the evidence gap, red-on-tint plus uppercase for "cannot be
               determined", which is the state an auditor is least likely to have a mental model for.

               -restarted was added 2026-08-11, and it re-opened the same defect on a fifth verdict:
               a restart-only chain emitted class="chain-restarted" into this stylesheet, which had
               no such rule, so the verdict cell rendered as body text again. The guard test now
               enumerates the status enum instead of a hand-written list of class names, so a sixth
               verdict fails here rather than shipping unstyled. It is amber-on-tint like
               -unverifiable and deliberately NOT green: a restart is not a clean bill of health. */
            .chain-intact { color: var(--green, #060); font-weight: 700; }
            .chain-broken { color: var(--red, #c00); font-weight: 700; }
            .chain-unverifiable { color: #8A5A00; font-weight: 700; background: #FFF4E0; padding: 2px 6px; border-left: 4px solid #E0A100; }
            .chain-restarted { color: #8A5A00; font-weight: 700; background: #FFF4E0; padding: 2px 6px; border-left: 4px solid #E0A100; }
            .chain-indeterminate { color: var(--red, #c00); font-weight: 800; text-transform: uppercase; background: #FDEDED; padding: 2px 6px; border-left: 4px solid #c00; }
            /* The class when verification threw and the cell reads "Could not verify" — the same
               "no verdict reached" family, and it must not read as body text either. */
            .chain-unknown { color: var(--red, #c00); font-weight: 800; background: #FDEDED; padding: 2px 6px; border-left: 4px solid #c00; }
            .rb-signature { border-top: 1px solid #E2E8E5; padding-top: 12px; }
            .mono { font-family: monospace; font-size: 10px; word-break: break-all; }
            @media print { body { margin: 0; padding: 8px; } }
            </style>
            </head>
            <body>
            """;

        private static string HtmlFoot() => "</body></html>";
    }
}
