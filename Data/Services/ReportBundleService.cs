/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            ILogger<ReportBundleService> logger,
            AuditLogService? auditLog = null)
        {
            _executiveHealth = executiveHealth ?? throw new ArgumentNullException(nameof(executiveHealth));
            _healthCheckService = healthCheckService ?? throw new ArgumentNullException(nameof(healthCheckService));
            _vaState = vaState ?? throw new ArgumentNullException(nameof(vaState));
            _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
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
            var allFindings = GetCachedFindings();
            var top5 = allFindings
                .Where(f => f.ThisServer == null || f.ThisServer.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => SeverityRank(f.Severity))
                .Take(5)
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
                    sb.Append($"<tr><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(f.DisplayName)}</td><td>{EscapeHtml(f.Category)}</td><td>{EscapeHtml(f.Message)}</td></tr>");
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

        /// <summary>
        /// Composes the DBA Handoff Package HTML and queues it for printing.
        /// </summary>
        public async Task<string> PrepareDbaHandoffHtmlAsync(string serverName)
        {
            var display = AnonymisedName(serverName);
            var health = _healthCheckService.GetCachedHealth(serverName);
            var allFindings = GetCachedFindings()
                .Where(f => f.ThisServer == null || f.ThisServer.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => SeverityRank(f.Severity))
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

            // All VA findings
            sb.Append("<section><h2>All Vulnerability Assessment Findings</h2>");
            if (allFindings.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No cached findings. Run a Vulnerability Scan first.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>ID</th><th>Severity</th><th>Check</th><th>Category</th><th>Message</th></tr></thead><tbody>");
                foreach (var f in allFindings)
                {
                    sb.Append($"<tr><td>{EscapeHtml(f.CheckId)}</td><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(f.DisplayName)}</td><td>{EscapeHtml(f.Category)}</td><td>{EscapeHtml(f.Message)}</td></tr>");
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
                    sb.Append($"<tr><td>{EscapeHtml(f.CheckId)}</td><td>{EscapeHtml(f.DisplayName)}</td><td>{EscapeHtml(f.Remediation)}</td></tr>");
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

        /// <summary>
        /// Composes the Audit Evidence HTML and queues it for printing.
        /// </summary>
        public async Task<string> PrepareAuditEvidenceHtmlAsync(string serverName)
        {
            var display = AnonymisedName(serverName);
            var allFindings = GetCachedFindings()
                .Where(f => f.ThisServer == null || f.ThisServer.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => SeverityRank(f.Severity))
                .ToList();

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

            // Compute SHA-256 of report body (findings list as stable text)
            var findingsText = string.Join("\n", allFindings.Select(f => $"{f.CheckId}|{f.Severity}|{f.DisplayName}|{f.Message}"));
            var sha256 = ComputeSha256(findingsText + $"|{display}|{DateTime.Now:yyyy-MM-dd}");

            var sb = new StringBuilder();
            sb.Append(HtmlHead("Audit Evidence"));
            sb.Append($"""
                <div class="rb-header">
                    <div class="rb-tag">Audit Evidence — for compliance review</div>
                    <h1>{EscapeHtml(display)}</h1>
                    <div class="rb-meta">Report period: last 30 days &nbsp;|&nbsp; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
                    <div class="rb-sha">SHA-256: {sha256}</div>
                </div>
                """);

            // VA findings with framework hooks
            sb.Append("<section><h2>Vulnerability Assessment Findings</h2>");
            if (allFindings.Count == 0)
            {
                sb.Append("<p class=\"rb-empty\">No cached findings. Run a Vulnerability Scan first.</p>");
            }
            else
            {
                sb.Append("<table class=\"rb-table\"><thead><tr><th>ID</th><th>Severity</th><th>Check</th><th>Category</th><th>Framework</th><th>Message</th></tr></thead><tbody>");
                foreach (var f in allFindings)
                {
                    // Framework mapping hook — placeholder until Compliance Framework feature ships
                    var framework = MapFramework(f.Category, f.CheckId);
                    sb.Append($"<tr><td>{EscapeHtml(f.CheckId)}</td><td class=\"sev sev-{f.Severity?.ToLowerInvariant()}\">{EscapeHtml(f.Severity)}</td><td>{EscapeHtml(f.DisplayName)}</td><td>{EscapeHtml(f.Category)}</td><td>{EscapeHtml(framework)}</td><td>{EscapeHtml(f.Message)}</td></tr>");
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
        private static string MapFramework(string? category, string? checkId)
        {
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
