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
    /// Generates diagnostic report bundles (Executive Summary, DBA Handoff, Audit Evidence).
    /// HTML is composed as plain strings and stored in-memory; the ReportBundles page renders
    /// the HTML in a print iframe that is then captured by PrintService.PrintToPdfAsync.
    /// Saves PDFs to the user Downloads folder.
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
        /// Composes the Executive Summary HTML and queues it for printing.
        /// Returns the pending HTML — caller should render it and invoke PrintService.
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
                    <div class="rb-meta">Report period: last 30 days &nbsp;|&nbsp; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
                </div>
                """);

            // Health score block
            var scoreClass = health.Score >= 70 ? "score-good" : health.Score >= 40 ? "score-warn" : "score-bad";
            sb.Append($"""
                <section>
                    <h2>Overall Health Score</h2>
                    <div class="score-block {scoreClass}">
                        <span class="score-number">{health.Score}</span><span class="score-label"> / 100</span>
                        <div class="score-msg">{EscapeHtml(health.Message ?? string.Empty)}</div>
                    </div>
                </section>
                """);

            // Top 5 risks
            sb.Append("<section><h2>Top 5 Risks</h2>");
            if (top5.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No cached vulnerability findings. Run a Vulnerability Scan first.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>Severity</th><th>Check</th><th>Category</th><th>Message</th></tr></thead><tbody>");
                foreach (var f in top5)
                {
                    var impact = string.IsNullOrWhiteSpace(f.BusinessImpact) ? f.Message : f.BusinessImpact;
                    sb.Append($"<tr><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(f.Name)}</td><td>{EscapeHtml(f.Category)}</td><td>{EscapeHtml(impact)}</td></tr>");
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
            var (rollup, _, serverCount) = GatherEstateRollup();
            var topRisks = rollup.Take(5).ToList();

            var sb = new StringBuilder();
            sb.Append(HtmlHead("Executive Summary — Estate"));
            sb.Append($"""
                <div class="rb-header">
                    <div class="rb-tag">Executive Summary — estate roll-up across {serverCount} server{(serverCount == 1 ? "" : "s")}</div>
                    <h1>All Servers</h1>
                    <div class="rb-meta">Report period: last 30 days &nbsp;|&nbsp; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
                </div>
                """);

            sb.Append("<section><h2>Top 5 Risks Across the Estate</h2>");
            if (topRisks.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No cached vulnerability findings. Run a Vulnerability Scan first.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>Severity</th><th>Check</th><th>Category</th><th>Servers Impacted</th></tr></thead><tbody>");
                foreach (var f in topRisks)
                {
                    sb.Append($"<tr><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(f.Name)}</td><td>{EscapeHtml(f.Category)}</td><td>{f.ServersImpacted} of {f.ServersTotal}</td></tr>");
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

            // All findings (Microsoft VA + corpus checks, deduped — source shown per row)
            sb.Append("<section><h2>All Diagnostic Findings</h2>");
            if (allFindings.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No cached findings. Run a Vulnerability Scan or check suite first.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>ID</th><th>Severity</th><th>Check</th><th>Category</th><th>Source</th><th>Message</th></tr></thead><tbody>");
                foreach (var f in allFindings)
                {
                    sb.Append($"<tr><td>{EscapeHtml(f.Id)}</td><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(f.Name)}</td><td>{EscapeHtml(f.Category)}</td><td>{EscapeHtml(f.Source)}</td><td>{EscapeHtml(f.Message)}</td></tr>");
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
            var (rollup, appendix, serverCount) = GatherEstateRollup();

            var sb = new StringBuilder();
            sb.Append(HtmlHead("DBA Handoff Package — Estate"));
            sb.Append($"""
                <div class="rb-header">
                    <div class="rb-tag">DBA Handoff — estate baseline across {serverCount} server{(serverCount == 1 ? "" : "s")}</div>
                    <h1>All Servers</h1>
                    <div class="rb-meta">Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
                </div>
                """);

            // Roll-up: one row per check, with the impacted-server count (NOT a per-server name list).
            sb.Append("<section><h2>Findings Across the Estate (grouped by check)</h2>");
            if (rollup.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No cached findings. Run a Vulnerability Scan first.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>ID</th><th>Severity</th><th>Check</th><th>Category</th><th>Source</th><th>Servers Impacted</th></tr></thead><tbody>");
                foreach (var f in rollup)
                {
                    sb.Append($"<tr><td>{EscapeHtml(f.Id)}</td><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(f.Name)}</td><td>{EscapeHtml(f.Category)}</td><td>{EscapeHtml(f.Source)}</td><td>{f.ServersImpacted} of {f.ServersTotal}</td></tr>");
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

            // Audit log summary: count events in last 30 days
            var auditCount = 0;
            var chainStatus = "N/A (audit service not available)";
            if (_auditLog != null)
            {
                try
                {
                    var from = DateTime.Now.AddDays(-30);
                    var to = DateTime.Now;
                    var entries = _auditLog.GetEntries(from, to);
                    auditCount = entries.Count;
                    chainStatus = _auditLog.ChainBroken ? "Broken" : "Intact";
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
                    <div class="rb-meta">Report period: last 30 days &nbsp;|&nbsp; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
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
            sb.Append($"""
                <section>
                    <h2>Audit Log Summary (Last 30 Days)</h2>
                    <table class="rb-table rb-kv">
                        <tr><th>Total Audit Events</th><td>{auditCount:N0}</td></tr>
                        <tr><th>HMAC Chain Status</th><td class="chain-{chainStatus.ToLowerInvariant()}">{EscapeHtml(chainStatus)}</td></tr>
                    </table>
                </section>
                """);

            // Signature block
            sb.Append($"""
                <section class="rb-signature">
                    <h2>Report Integrity</h2>
                    <table class="rb-table rb-kv">
                        <tr><th>Generated By</th><td>SQLTriage Diagnostic Report Packages v1</td></tr>
                        <tr><th>Generated</th><td>{DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)</td></tr>
                        <tr><th>Report Period</th><td>Last 30 days</td></tr>
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
                Score = health.Score,
                ScoreMessage = health.Message ?? string.Empty,
                TopRisks = top5,
            };
            return await Task.Run(() => AssessmentPdf.BuildExecutiveSummaryBundle(dto)).ConfigureAwait(false);
        }

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

        /// <summary>Estate-wide Executive Summary PDF: top risks ranked by servers impacted.</summary>
        public async Task<byte[]> BuildExecutiveSummaryEstatePdfAsync(bool watermark)
        {
            var (rollup, _, serverCount) = GatherEstateRollup();
            var dto = new ExecutiveSummaryBundle
            {
                Meta = BuildMeta($"All Servers ({serverCount})", "Executive Summary — Estate", watermark),
                Score = 0,
                ScoreMessage = $"Estate roll-up across {serverCount} server{(serverCount == 1 ? "" : "s")}.",
                TopRisks = rollup.Take(5).ToList(),
            };
            return await Task.Run(() => AssessmentPdf.BuildExecutiveSummaryBundle(dto)).ConfigureAwait(false);
        }

        /// <summary>Estate-wide DBA Handoff PDF: roll-up by check + per-server appendix.</summary>
        public async Task<byte[]> BuildDbaHandoffEstatePdfAsync(bool watermark)
        {
            var (rollup, appendix, serverCount) = GatherEstateRollup();
            var dto = new DbaHandoffBundle
            {
                Meta = BuildMeta($"All Servers ({serverCount})", "DBA Handoff Package — Estate", watermark),
                Inventory = new List<(string, string)> { ("Scope", $"Estate roll-up — {serverCount} server{(serverCount == 1 ? "" : "s")} scanned") },
                AllFindings = rollup,
                KnownIssues = new List<BundleFinding>(),
                EstateAppendix = appendix,
            };
            return await Task.Run(() => AssessmentPdf.BuildDbaHandoffBundle(dto)).ConfigureAwait(false);
        }

        public async Task<byte[]> BuildAuditEvidencePdfAsync(string serverName, bool watermark)
        {
            var display = AnonymisedName(serverName);
            var (allFindings, sha256, scanSha256) = GatherAuditEvidence(serverName, display);

            var auditCount = 0;
            var chainStatus = "N/A (audit service not available)";
            if (_auditLog != null)
            {
                try
                {
                    var entries = _auditLog.GetEntries(DateTime.Now.AddDays(-30), DateTime.Now);
                    auditCount = entries.Count;
                    chainStatus = _auditLog.ChainBroken ? "Broken" : "Intact";
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
                AuditEventCount = auditCount,
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
        /// Audit Evidence for the whole estate: one per-server PDF (each with its own per-server
        /// dual hash), packed into a single zip. Keeps every server's attestation independently
        /// verifiable — an aggregate hash over mixed servers would be weaker evidence. Returns the
        /// zip bytes; a server with no findings is skipped.
        /// </summary>
        public async Task<byte[]> BuildAuditEvidenceEstateZipAsync(bool watermarkAll)
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
                sb.Append("<p class=\"rb-empty\">No outstanding bad-state risks. Run a Vulnerability Scan first, or the estate is clean.</p>");
            }
            else
            {
                var impactedHead = estate ? "<th>Servers</th>" : "";
                sb.Append($"<table class=\"rb-table\"><thead><tr><th>ID</th><th>Severity</th><th>Risk</th><th>Category</th><th>Owner</th><th>Review by</th>{impactedHead}<th>Business Impact</th></tr></thead><tbody>");
                foreach (var r in b.Rows)
                {
                    var impactedCell = estate ? $"<td>{r.ServersImpacted} of {r.ServersTotal}</td>" : "";
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

        private AssessmentMeta BuildMeta(string display, string title, bool watermark)
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

            return new BundleFinding
            {
                Id = f.CheckId ?? string.Empty,
                Severity = f.Severity ?? string.Empty,
                Name = f.DisplayName ?? string.Empty,
                Category = enrichedCategory,
                Message = corpusCheck?.Description ?? f.Message ?? string.Empty,
                Remediation = corpusCheck?.DetailedRemediation ?? corpusCheck?.RecommendedAction ?? f.Remediation ?? string.Empty,
                // Use the canonical corpus Id (when matched) so FrameworkMappings — keyed on SqlCheck.Id — resolves even when f.CheckId is a legacy id.
                Framework = MapFramework(enrichedCategory, corpusCheck?.Id ?? f.CheckId),
                Status = f.Status ?? string.Empty,
                Source = string.IsNullOrEmpty(f.Source) ? "Microsoft VA" : f.Source,
                // Client-facing voice (corpus '## Business Impact'); business-audience bundles
                // render this instead of the Intent-derived Message (which can carry oracle notes).
                BusinessImpact = corpusCheck?.BusinessImpact ?? string.Empty,
            };
        }

        /// <summary>
        /// Records a successful generation (called by the UI after PrintService succeeds).
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

        private static int SeverityRank(string? severity) => severity?.ToLowerInvariant() switch
        {
            "error" or "critical" or "high" => 3,
            "warning" or "medium" => 2,
            "information" or "info" or "low" => 1,
            _ => 0
        };

        /// <summary>Server label used when a finding carries no ThisServer attribution.</summary>
        private const string UnattributedServer = "(unattributed)";

        /// <summary>
        /// Aggregates the cached findings across the whole scanned estate for an All-Servers
        /// report. Rather than one row per (server × finding) — which bloats on a large estate —
        /// findings are grouped by CheckId into one roll-up row carrying a distinct-server count
        /// ("Servers Impacted: N of M"). A per-server appendix lists each server's impacted checks
        /// once, so full detail is preserved without inflating the hot table. Null-attribution
        /// findings fall into a single "(unattributed)" bucket — never fanned across all servers.
        /// </summary>
        private (List<BundleFinding> Rollup, List<EstateServerEntry> Appendix, int ServerCount) GatherEstateRollup()
        {
            var raw = GetMergedFindings(null);

            // Distinct scanned servers = the denominator for "N of M".
            var servers = raw
                .Select(f => string.IsNullOrEmpty(f.ThisServer) ? UnattributedServer : f.ThisServer!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var serverCount = servers.Count;

            // Group by check (CheckId, falling back to DisplayName for unkeyed legacy rows).
            var rollup = new List<BundleFinding>();
            foreach (var grp in raw.GroupBy(f => !string.IsNullOrEmpty(f.CheckId) ? f.CheckId! : f.DisplayName ?? string.Empty,
                                            StringComparer.OrdinalIgnoreCase))
            {
                // Representative row = the highest-severity instance, then enriched from the corpus.
                var rep = grp.OrderByDescending(f => SeverityRank(f.Severity)).First();
                var bf = ToBundleFinding(rep);

                bf.ServersImpacted = grp
                    .Select(f => string.IsNullOrEmpty(f.ThisServer) ? UnattributedServer : f.ThisServer!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
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

            return (rollup, appendix, serverCount);
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

            // Merged source (VA + corpus failures) so the Risk Register reflects the full
            // diagnostic picture, not just the MS VA subset.
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
                        var rep = g.OrderByDescending(f => SeverityRank(f.Severity)).First();
                        return BuildRiskRow(rep, Lookup(rep),
                            impacted: g.Select(f => string.IsNullOrEmpty(f.ThisServer) ? UnattributedServer : f.ThisServer!)
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
        /// shape, tagged Source="Corpus". Status maps Passed→"Passed" else "Failed".
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
            Status = r.Passed ? "Passed" : "Failed",
            ThisServer = string.IsNullOrEmpty(r.InstanceName) ? null : r.InstanceName,
            Source = "Corpus",
        };

        private static string MergeKey(AssessmentResult f) =>
            $"{f.ThisServer}|{f.CheckId}".ToLowerInvariant();

        /// <summary>
        /// Returns the operational finding set for a report: the MS VA findings UNION the
        /// failing corpus check results, deduped by (server, checkId) preferring the corpus
        /// row (richer + curated; the VA row's evidence is corroborating). serverName null =
        /// estate roll-up (corpus pulled per VA-known server). Falls back to VA-only when the
        /// corpus execution source is unavailable.
        ///
        /// NOTE: Audit Evidence deliberately does NOT use this — it stays a single-source MS VA
        /// attestation with its own verifiable hash. Corpus completeness lives in the
        /// operational bundles (Executive Summary / DBA Handoff / Risk Register).
        ///
        /// Only FAILING corpus results are merged: a "finding" in these bundles is a problem to
        /// act on, and this avoids bloating the report with hundreds of passing checks. The
        /// SQLite-hydrate fidelity gap (IsBad/effort dropped) does not bite here — the Risk
        /// Register backfills IsBad from the corpus index.
        /// </summary>
        private List<AssessmentResult> GetMergedFindings(string? serverName)
        {
            var va = GetCachedFindings()
                .Where(f => serverName == null || f.ThisServer == null
                            || f.ThisServer.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (_checkExecution == null) return va;

            try
            {
                // Estate (serverName == null): seed the server set from VA-discovered servers
                // UNION servers that have cached corpus results (in-memory + persisted on disk).
                // Without the corpus union, an estate that ran the check suite but never a
                // Microsoft VA scan discovers zero servers, so the roll-up comes back empty
                // despite cached corpus findings — the "run a VA first" false-empty.
                var servers = !string.IsNullOrEmpty(serverName)
                    ? new List<string> { serverName }
                    : va.Select(f => f.ThisServer)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Cast<string>()
                        .Concat(_checkExecution.GetServersWithResults())
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

                if (corpus.Count == 0) return va;

                var corpusKeys = new HashSet<string>(corpus.Select(MergeKey), StringComparer.OrdinalIgnoreCase);
                var merged = new List<AssessmentResult>(corpus);
                merged.AddRange(va.Where(f => !corpusKeys.Contains(MergeKey(f))));
                return merged;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not merge corpus findings into report bundle; using VA findings only");
                return va;
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
            body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 11px; color: #1a1a2e; margin: 0; padding: 16px 24px; background: #fff; }
            .rb-header { border-bottom: 2px solid #1a1a2e; padding-bottom: 12px; margin-bottom: 20px; }
            .rb-tag { font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .08em; color: #5a5a7a; margin-bottom: 4px; }
            h1 { margin: 0 0 4px; font-size: 20px; font-weight: 700; }
            h2 { font-size: 13px; font-weight: 700; margin: 0 0 8px; border-bottom: 1px solid #d0d0e0; padding-bottom: 4px; }
            .rb-meta { font-size: 10px; color: #5a5a7a; }
            .rb-sha { font-size: 9px; font-family: monospace; color: #888; margin-top: 4px; word-break: break-all; }
            section { margin-bottom: 20px; page-break-inside: avoid; }
            .rb-table { border-collapse: collapse; width: 100%; margin-bottom: 8px; }
            .rb-table th, .rb-table td { border: 1px solid #d0d0e0; padding: 4px 8px; text-align: left; vertical-align: top; }
            .rb-table thead th { background: #f0f0f8; font-weight: 700; }
            .rb-table tr:nth-child(even) { background: #f8f8fc; }
            .rb-kv th { width: 200px; font-weight: 700; background: #f0f0f8; }
            .sev-error, .sev-critical, .sev-high { color: var(--red, #c00); font-weight: 700; }
            .sev-warning, .sev-medium { color: var(--orange, #c60); font-weight: 600; }
            .sev-information, .sev-info, .sev-low { color: var(--green, #060); }
            .score-block { display: inline-block; border: 2px solid #d0d0e0; border-radius: 8px; padding: 12px 24px; margin: 8px 0; }
            .score-good { border-color: var(--green, #4caf50); }
            .score-warn { border-color: var(--orange, #ff9800); }
            .score-bad { border-color: var(--red, #f44336); }
            .score-number { font-size: 32px; font-weight: 900; }
            .score-label { font-size: 16px; color: #5a5a7a; }
            .score-msg { font-size: 11px; color: #5a5a7a; margin-top: 4px; }
            .rb-empty { color: #888; font-style: italic; }
            .rb-placeholder { color: #888; font-style: italic; background: #f8f8fc; padding: 12px; border-radius: 4px; border: 1px dashed #d0d0e0; }
            .chain-intact { color: var(--green, #060); font-weight: 700; }
            .chain-broken { color: var(--red, #c00); font-weight: 700; }
            .rb-signature { border-top: 1px solid #d0d0e0; padding-top: 12px; }
            .mono { font-family: monospace; font-size: 10px; word-break: break-all; }
            @media print { body { margin: 0; padding: 8px; } }
            </style>
            </head>
            <body>
            """;

        private static string HtmlFoot() => "</body></html>";
    }
}
