/* In the name of God, the Merciful, the Compassionate */
/*
 * ScheduledReportService — MSP #5 headless report renderer.
 *
 * This is the PURE-QuestPDF path a background ScheduledTaskEngine Report task runs. Every method
 * here maps posture data → a BriefingReport → AssessmentPdf.BuildExecutiveBriefing (QuestPDF),
 * then writes the bytes to output\reports\. There is DELIBERATELY no dependency on PrintService /
 * CoreWebView2 / any UI-thread type: ReportBundleService's browser-print path CANNOT run from a
 * background task (headless, no message pump), so the report lane uses the QuestPDF builders only.
 *
 * Content is score/posture-level (check name · category · severity · plain-English recommendation).
 * It never surfaces raw query or plan text — design §4.5 honesty contract.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>Static, DI-free helpers for the MSP #5 scheduled report. Kept static so the render
    /// path is provably free of any UI/WebView2 dependency and can run on a bare thread-pool thread.</summary>
    public static class ScheduledReportService
    {
        /// <summary>The established reports root: AppContext.BaseDirectory\output\reports\.</summary>
        public static string ReportsFolder =>
            Path.Combine(AppContext.BaseDirectory, "output", "reports");

        /// <summary>Map one posture <see cref="CheckResult"/> to a report <see cref="FindingRow"/>.
        /// Identical precedence to the interactive Executive Briefing (error → skip → warn → info →
        /// pass → accepted → fail) so the scheduled artifact matches what an operator sees on-screen.
        /// Ruling #4 (2026-07-20): the Warn arm must sit ABOVE the r.Passed arm — WARN now rides
        /// Passed=true (CheckExecutionService), so without it every "could not fully assess" result
        /// would export as a flat green Pass in an unattended client-facing PDF.
        ///
        /// <para>reports-r2-03 (2026-08-27): this ladder had NO Info arm and carried its own private
        /// skip test that only looked at the Message prefix, while the doc comment above claimed
        /// "identical precedence to the interactive Executive Briefing" — which was false. It was the
        /// 4th copy of one rule, and the two that had already been fixed on 2026-07-16
        /// (Pages/QuickCheck.razor and Cli/CsvResultWriter.cs) both delegate to
        /// <see cref="CheckClassification"/>. Measured before the fix on this UNATTENDED, headless
        /// path: a Verdict-INFO result mapped to FindingState.Pass and rode the rate ("75% Passed,
        /// 3 of 4 checks passed"), and a Verdict-SKIP result did the same, because a verdict-contract
        /// check's Message comes from its own template and carries no "SKIP" prefix
        /// (CheckExecutionService) — the exact corpus shape CheckClassification.IsSkip was hardened
        /// against (53 of 55 Verdict-SKIP corpus results, e.g. SQLT-CORE-00580 "No AGs configured.").
        /// The ladder now delegates, so this copy cannot drift from the other three again.</para></summary>
        private static FindingRow ToFindingRow(CheckResult r)
        {
            var state = (r.IsCorrupted || !string.IsNullOrEmpty(r.ErrorMessage)) ? FindingState.Error
                      : CheckClassification.IsSkip(r)                            ? FindingState.Skipped
                      : CheckClassification.IsWarn(r)                            ? FindingState.Warn
                      : CheckClassification.IsInfo(r)                            ? FindingState.Info
                      : r.Passed                                                 ? FindingState.Pass
                      : r.IsAccepted                                             ? FindingState.Accepted
                      :                                                            FindingState.Fail;

            return new FindingRow
            {
                State          = state,
                Name           = r.CheckName,
                Category       = r.Category,
                Severity       = r.Severity,
                Server         = r.InstanceName,
                Detail         = r.Message,
                Recommendation = !string.IsNullOrWhiteSpace(r.RecommendedAction)
                                    ? r.RecommendedAction!
                                    : (r.Description ?? ""),
                BusinessImpact = r.BusinessImpact ?? "",
            };
        }

        /// <summary>Build the Executive Briefing DTO from posture results across the in-scope estate.</summary>
        public static BriefingReport BuildBriefing(AssessmentMeta meta, IEnumerable<CheckResult> results)
        {
            var report = new BriefingReport { Meta = meta };
            foreach (var r in results)
                report.Findings.Add(ToFindingRow(r));
            return report;
        }

        /// <summary>Render the report DTO to PDF bytes. Switches on <see cref="ReportKind"/>; every
        /// case is a pure QuestPDF builder. THIS is the headless render pin — no WebView2 anywhere.</summary>
        public static byte[] Render(ReportKind kind, BriefingReport report) =>
#if SQLT_NO_REPORT_EXEC_BRIEFING
            // Executive Briefing (and therefore the scheduled report that renders one) is gated out
            // of this edition (Adrian's ruling 2026-07-21). The builder is compile-pruned; a
            // scheduled Report task fails closed with this message via the engine's best-effort catch.
            throw new NotSupportedException("Executive report generation is not included in this edition.");
#else
            kind switch
            {
                ReportKind.ExecutiveBriefing => AssessmentPdf.BuildExecutiveBriefing(report),
                _                            => AssessmentPdf.BuildExecutiveBriefing(report),
            };
#endif

        /// <summary>Write PDF bytes to output\reports\ under a sanitized, timestamped name and return
        /// the absolute path. Creates the folder if absent.</summary>
        public static string SaveToReportsFolder(byte[] pdf, string label, DateTime nowUtc, string runId)
        {
            Directory.CreateDirectory(ReportsFolder);
            var safeLabel = SanitizeFileName(label);
            var fileName = $"ExecutiveBriefing_{safeLabel}_{nowUtc:yyyyMMdd_HHmmssZ}_{runId}.pdf";
            var path = Path.Combine(ReportsFolder, fileName);
            File.WriteAllBytes(path, pdf);
            return path;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "estate";
            var sanitized = name.Replace("\\", "_").Replace("/", "_");
            foreach (var c in Path.GetInvalidFileNameChars())
                sanitized = sanitized.Replace(c, '_');
            return sanitized.Length > 80 ? sanitized[..80] : sanitized;
        }
    }
}
