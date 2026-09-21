/* In the name of God, the Merciful, the Compassionate */
/*
 * HaDrPostureReportService — composes the "HA/DR & Backup Posture" report DTO by recomposing
 * data that ALREADY exists elsewhere in the app: corpus check results (CheckExecutionService),
 * the RestoreVerify (MSP #9) results artifact, and the cached ServerDocs HA/DR snapshot. It never
 * runs a new probe, a new SQL query, or a new RESTORE VERIFYONLY — every number traces back to a
 * check/run that already happened. AssessmentPdf.BuildHaDrPostureReport renders the DTO this
 * service produces. Single-server only (no estate/All-Servers roll-up yet).
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services;

public sealed class HaDrPostureReportService
{
    private readonly CheckExecutionService _checkExecution;
    private readonly ScheduledTaskDefinitionService _taskDefs;
    private readonly ScheduledTaskHistoryService _taskHistory;
    private readonly ServerDocumentationService _serverDocs;
    private readonly UserSettingsService _userSettings;
    private readonly ILogger<HaDrPostureReportService> _logger;

    public HaDrPostureReportService(
        CheckExecutionService checkExecution,
        ScheduledTaskDefinitionService taskDefs,
        ScheduledTaskHistoryService taskHistory,
        ServerDocumentationService serverDocs,
        UserSettingsService userSettings,
        ILogger<HaDrPostureReportService> logger)
    {
        _checkExecution = checkExecution ?? throw new ArgumentNullException(nameof(checkExecution));
        _taskDefs = taskDefs ?? throw new ArgumentNullException(nameof(taskDefs));
        _taskHistory = taskHistory ?? throw new ArgumentNullException(nameof(taskHistory));
        _serverDocs = serverDocs ?? throw new ArgumentNullException(nameof(serverDocs));
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _logger = logger;
    }

    /// <summary>Composes the report DTO for one server: Backups, Availability Groups &amp;
    /// Failover, Quorum, and Restore Verification, each with a pass-% verdict recomposed from
    /// checks/results that already ran.</summary>
    public async Task<HaDrPostureReport> BuildReportAsync(string serverName)
    {
        var report = new HaDrPostureReport { Meta = BuildMeta(serverName, watermark: false) };

        var allResults = _checkExecution.GetResults(serverName, maxCount: 2000);
        var haDrDoc = await LoadHaDrDocAsync(serverName).ConfigureAwait(false);

        report.Sections.Add(BuildBackupsSection(allResults, serverName));
        report.Sections.Add(BuildAvailabilitySection(allResults, haDrDoc, serverName));
        report.Sections.Add(BuildQuorumSection(allResults, serverName));
        report.Sections.Add(BuildRestoreVerifySection(serverName));

        return report;
    }

    /// <summary>Builds the report and renders it to PDF bytes in one call — the entry point
    /// ReportBundles.razor's Save-as-PDF button uses.</summary>
    public async Task<byte[]> BuildPdfAsync(string serverName, bool watermark)
    {
#if SQLT_NO_REPORT_HADR_POSTURE
        // Gated out of this edition (Adrian's ruling 2026-07-21). The report builder is
        // compile-pruned from AssessmentPdf; this wrapper stays only so the type signature is
        // stable, and it is unreachable because the ReportBundles trigger is compile-pruned too.
        await Task.CompletedTask.ConfigureAwait(false);
        throw new NotSupportedException("The HA/DR & Backup Posture report is not included in this edition.");
#else
        var report = await BuildReportAsync(serverName).ConfigureAwait(false);
        report.Meta.Watermark = watermark;
        return AssessmentPdf.BuildHaDrPostureReport(report);
#endif
    }

    private AssessmentMeta BuildMeta(string serverName, bool watermark)
    {
        var nowUtc = DateTime.UtcNow;
        var tz = TimeZoneInfo.Local.StandardName;
        var runId = Guid.NewGuid().ToString("N")[..8];
        return new AssessmentMeta
        {
            Title = "HA/DR & Backup Posture",
            Company = _userSettings.GetReportCompanyName(),
            Subtitle = serverName,
            Engine = "Corpus checks + restore-verify + server documentation",
            GeneratedUtc = nowUtc.ToString("yyyy-MM-ddTHH:mmZ"),
            TimezoneId = tz,
            RunId = runId,
            ColorBlind = _userSettings.GetColorBlindMode(),
            Watermark = watermark,
            FooterMeta = $"SQLTriage — HA/DR & Backup Posture — {serverName} — {nowUtc:yyyy-MM-ddTHH:mmZ} ({tz}) — Run {runId}",
        };
    }

    // ── ServerDocs snapshot (cached, disk-only — never a live SMO capture) ──────────────────

    private async Task<DocSection?> LoadHaDrDocAsync(string serverName)
    {
        try
        {
            var snap = await _serverDocs.LoadLatestAsync(serverName).ConfigureAwait(false);
            return snap?.Sections.FirstOrDefault(s => string.Equals(s.Id, "hadr", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load cached HA/DR server-docs snapshot for {Server}", serverName);
            return null;
        }
    }

    // ── Corpus-check classification ──────────────────────────────────────────────────────────
    // Mirrors AssessmentPdf.FocusAreaFor's tolerant, keyword-based idiom, anchored on the real
    // category strings GovernanceService.CategoryMapping already keys on — "Backup" and
    // "Availability" — with a keyword sub-split for the two meanings "Availability" covers:
    // AG/failover vs. cluster quorum. Tunable here as corpus category naming drifts.

    private enum HaDrBucket { None, Backups, AvailabilityFailover, Quorum }

    private static HaDrBucket Classify(CheckResult r)
    {
        var category = (r.Category ?? "").Trim();
        var text = $"{r.Category} {r.CheckName} {r.Message}".ToLowerInvariant();
        bool Has(params string[] keys) => keys.Any(k => text.Contains(k));

        if (string.Equals(category, "Backup", StringComparison.OrdinalIgnoreCase))
            return HaDrBucket.Backups;

        if (string.Equals(category, "Availability", StringComparison.OrdinalIgnoreCase))
            return Has("quorum", "witness server", "failover cluster instance")
                ? HaDrBucket.Quorum
                : HaDrBucket.AvailabilityFailover;

        // Fallback for other categories (Reliability/DefaultRuleset/Custom) that still carry an
        // HA/DR-relevant check.
        if (Has("quorum", "witness server", "failover cluster instance"))
            return HaDrBucket.Quorum;
        if (Has("availability group", "alwayson", "always on", "failover", "readable secondary"))
            return HaDrBucket.AvailabilityFailover;
        if (Has("backup", "log chain", "recovery model"))
            return HaDrBucket.Backups;

        return HaDrBucket.None;
    }

    private static int SeverityRank(string? sev) => (sev ?? "").Trim().ToLowerInvariant() switch
    {
        "critical" => 0, "high" => 1, "warning" => 2, "medium" or "moderate" => 3,
        "low" => 4, "info" or "information" or "informational" => 5, _ => 6,
    };

    private static FindingRow ToFindingRow(CheckResult r, string serverName) => new()
    {
        State = FindingState.Fail,
        Name = string.IsNullOrWhiteSpace(r.CheckName) ? r.CheckId : r.CheckName,
        Category = r.Category ?? "",
        Severity = r.Severity ?? "",
        Server = serverName,
        Detail = r.Message ?? "",
        BusinessImpact = r.BusinessImpact ?? "",
        Recommendation = r.RecommendedAction ?? "",
    };

    /// <summary>Buckets the already-collected result set for one server into a section, honouring
    /// the Skip/Info exclusion <see cref="CheckClassification"/> already uses everywhere else
    /// (governance, /cio, /dba, Executive Health) so this report's pass-% math never diverges from
    /// what those surfaces show for the same checks.</summary>
    private HaDrSectionRow BuildCorpusSection(
        string id, string name, IReadOnlyList<CheckResult> allResults, HaDrBucket bucket,
        string genericEmptyText, string contextLine, string serverName)
    {
        var row = new HaDrSectionRow { Id = id, Name = name, ContextLine = contextLine };
        var members = allResults.Where(r => Classify(r) == bucket).ToList();

        if (members.Count == 0)
        {
            row.Total = 0;
            row.Percent = -1;
            row.EmptyStateText = genericEmptyText;
            return row;
        }

        var scorable = members.Where(CheckClassification.IsScorable).ToList();
        if (scorable.Count == 0)
        {
            // Every check in this bucket returned SKIP/INFO/ERROR — honest, not a fail. Prefer the real
            // skip/info message (e.g. corpus SQLT-CORE-00580 "No AGs configured.") so the report says
            // exactly what the corpus already determined.
            row.Total = 0;
            row.Percent = -1;
            var msg = members.Select(m => (m.Message ?? "").Trim()).FirstOrDefault(m => m.Length > 0);
            if (!string.IsNullOrWhiteSpace(msg))
            {
                row.EmptyStateText = msg;
                return row;
            }

            // No informational skip/info message survived. If the bucket's checks ERRORED
            // (ErrorMessage set, Message empty — IsSkip treats an execution error as non-scorable),
            // the area could NOT be assessed. Say exactly that, rather than falling through to a
            // snapshot-derived "no AGs configured" line — that would be a false empty, claiming
            // knowledge (0 AGs) the check never actually confirmed this scan.
            var errored = members.Select(m => (m.ErrorMessage ?? "").Trim()).FirstOrDefault(m => m.Length > 0);
            row.EmptyStateText = string.IsNullOrWhiteSpace(errored)
                ? genericEmptyText
                : $"This area could not be checked: {errored}";
            return row;
        }

        var passed = scorable.Count(CheckClassification.CountsAsPass);
        row.Total = scorable.Count;
        row.Passed = passed;
        row.Percent = 100.0 * passed / scorable.Count;
        row.OpenFindings = scorable
            .Where(r => !CheckClassification.CountsAsPass(r))
            .OrderBy(r => SeverityRank(r.Severity))
            .ThenBy(r => r.CheckName, StringComparer.OrdinalIgnoreCase)
            .Select(r => ToFindingRow(r, serverName))
            .ToList();
        return row;
    }

    private HaDrSectionRow BuildBackupsSection(IReadOnlyList<CheckResult> allResults, string serverName) =>
        BuildCorpusSection(
            "backups", "Backups", allResults, HaDrBucket.Backups,
            "No backup-related checks have been run for this server yet.",
            "", serverName);

    private HaDrSectionRow BuildAvailabilitySection(IReadOnlyList<CheckResult> allResults, DocSection? haDrDoc, string serverName)
    {
        var noAgConfigured = haDrDoc?.Rows.Any(r =>
            string.Equals(r.Label, "Availability groups", StringComparison.OrdinalIgnoreCase) &&
            r.Value == "0") == true;
        var generic = noAgConfigured
            ? "No availability groups are configured on this server."
            : "No availability-group / failover checks have been run for this server yet.";
        var context = haDrDoc?.SummaryLine ?? "";
        return BuildCorpusSection(
            "ag-failover", "Availability Groups & Failover", allResults, HaDrBucket.AvailabilityFailover,
            generic, context, serverName);
    }

    private HaDrSectionRow BuildQuorumSection(IReadOnlyList<CheckResult> allResults, string serverName) =>
        BuildCorpusSection(
            "quorum", "Quorum", allResults, HaDrBucket.Quorum,
            "No quorum checks have been run for this server, or it is not part of a Windows Server Failover Cluster.",
            "", serverName);

    // ── Restore Verification (MSP #9) — a separate source entirely, not a corpus check ──────

    private HaDrSectionRow BuildRestoreVerifySection(string serverName)
    {
        var row = new HaDrSectionRow { Id = "restore-verify", Name = "Restore Verification" };

        var exec = FindLatestRestoreVerifyExecution(serverName);
        if (exec == null)
        {
            row.Total = 0;
            row.Percent = -1;
            row.EmptyStateText =
                "Restore verification has not been configured or run for this server. Add a Restore " +
                "Verify scheduled task to confirm that recent backups actually restore.";
            return row;
        }

        row.ContextLine = exec.CompletedAt.HasValue
            ? $"Last run {exec.CompletedAt.Value:yyyy-MM-dd HH:mm} UTC"
            : "";

        var summary = RestoreVerifyService.ReadResultsFile(exec.CsvFilePath);
        if (summary == null)
        {
            if (exec.RowCount == 0)
            {
                row.Total = 0;
                row.Percent = -1;
                row.EmptyStateText = "The last restore-verify run checked zero databases.";
                return row;
            }

            if (exec.Status == "Success")
            {
                // Success unambiguously means every database checked verified clean — no per-DB
                // detail is needed to state that honestly, even if the results artifact itself is
                // no longer readable.
                row.Total = exec.RowCount;
                row.Passed = exec.RowCount;
                row.Percent = 100.0;
                return row;
            }

            // Warning/Failed without a readable artifact: we know something was NOT a clean pass
            // but not the exact split — showing a percent here would be a guess dressed as fact.
            row.Total = 0;
            row.Percent = -1;
            row.EmptyStateText = !string.IsNullOrWhiteSpace(exec.ErrorMessage)
                ? exec.ErrorMessage!
                : $"The last restore-verify run ({exec.Status}) could not be read back in detail.";
            return row;
        }

        var s = summary.Value;
        var denominator = s.Passed + s.Failed + s.CouldNotRun;
        if (denominator == 0)
        {
            row.Total = 0;
            row.Percent = -1;
            row.EmptyStateText = s.NotSupported > 0
                ? $"All {s.NotSupported} database(s) checked use backup media outside the lite-tier " +
                  "restore-verify scope (URL/Azure-blob or non-disk) — not a failure, just not checkable yet."
                : "The last restore-verify run checked zero databases.";
            return row;
        }

        row.Total = denominator;
        row.Passed = s.Passed;
        row.Percent = 100.0 * s.Passed / denominator;
        if (s.NotSupported > 0)
            row.ContextLine = (string.IsNullOrEmpty(row.ContextLine) ? "" : row.ContextLine + "  ·  ") +
                $"{s.NotSupported} database(s) outside lite-tier scope (not counted)";

        row.OpenFindings = s.Items
            .Where(i => i.Outcome == RestoreVerifyOutcome.Failed || i.Outcome == RestoreVerifyOutcome.CouldNotRun)
            .OrderBy(i => i.Outcome == RestoreVerifyOutcome.Failed ? 0 : 1)
            .ThenBy(i => i.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .Select(i => new FindingRow
            {
                State = FindingState.Fail,
                Name = i.DatabaseName,
                Category = "Restore Verification",
                Severity = i.Outcome == RestoreVerifyOutcome.Failed ? "Critical" : "Warning",
                Server = serverName,
                Detail = i.Reason,
            })
            .ToList();

        return row;
    }

    /// <summary>Finds the most recent completed execution of a RestoreVerify task targeting this
    /// server — either a task pinned to this server, or an "all enabled servers" task (empty
    /// ServerName on the definition) that produced an execution row for it. Reads only the
    /// existing task-history store; never triggers a run.</summary>
    private ScheduledTaskExecution? FindLatestRestoreVerifyExecution(string serverName)
    {
        try
        {
            var taskIds = _taskDefs.GetAllTasks()
                .Where(t => t.TaskType == TaskType.RestoreVerify &&
                            (string.IsNullOrEmpty(t.ServerName) ||
                             string.Equals(t.ServerName, serverName, StringComparison.OrdinalIgnoreCase)))
                .Select(t => t.Id)
                .ToList();
            if (taskIds.Count == 0) return null;

            ScheduledTaskExecution? latest = null;
            foreach (var taskId in taskIds)
            {
                var candidate = _taskHistory.GetExecutionsByTask(taskId, maxRecords: 25)
                    .Where(e => string.Equals(e.ServerName, serverName, StringComparison.OrdinalIgnoreCase)
                                && e.CompletedAt.HasValue)
                    .OrderByDescending(e => e.CompletedAt)
                    .FirstOrDefault();
                if (candidate != null && (latest == null || candidate.CompletedAt > latest.CompletedAt))
                    latest = candidate;
            }
            return latest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not look up restore-verify history for {Server}", serverName);
            return null;
        }
    }
}
