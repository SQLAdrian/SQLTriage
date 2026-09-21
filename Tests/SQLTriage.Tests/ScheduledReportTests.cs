/* In the name of God, the Merciful, the Compassionate */
/*
 * MSP #5 — scheduled branded exec/estate report.
 *
 * Synthetic-data tests. NO SQL, NO client data, NO network — fabricated DTOs only.
 * Pins:
 *   1) HEADLINE — TaskType.Report renders a PDF via the PURE-QuestPDF path with NO WebView2 /
 *      UI-thread dependency (proven by rendering on a bare MTA background thread with no
 *      SynchronizationContext — a CoreWebView2 print would need an STA pump and would deadlock here).
 *   2) The email path attaches the file in BOTH the SMTP and Graph shapes.
 *   3) The cadence due-check fires only on schedule.
 *   4) Opt-in — a fresh install with no created task never runs a report.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MimeKit;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class ScheduledReportTests
    {
        static ScheduledReportTests()
        {
            // App.xaml.cs sets this at startup; tests bypass startup, so set it here.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        internal static AssessmentMeta Meta() => new()
        {
            Title = "Estate Report",
            Company = "Synthetic Test Co",
            Subtitle = "2 servers · SYN-SQL1, SYN-SQL2",
            Engine = "Corpus audit checks",
            GeneratedUtc = "2026-07-09T00:00Z",
            TimezoneId = "NZST",
            RunId = "synth001",
            ColorBlind = false,
        };

        internal static List<CheckResult> SampleResults() => new()
        {
            new() { CheckName = "TDE enabled", Category = "Security", Severity = "High",
                    Passed = false, InstanceName = "SYN-SQL1", Message = "TDE is off on 2 databases",
                    RecommendedAction = "Enable Transparent Data Encryption.", BusinessImpact = "Data-at-rest exposure." },
            new() { CheckName = "Recent full backup", Category = "Availability", Severity = "Critical",
                    Passed = false, InstanceName = "SYN-SQL1", Message = "Last full backup > 7 days",
                    RecommendedAction = "Restore a nightly full-backup schedule." },
            new() { CheckName = "Index fragmentation", Category = "Performance", Severity = "Medium",
                    Passed = true, InstanceName = "SYN-SQL2", Message = "OK" },
            new() { CheckName = "SA account renamed", Category = "Security", Severity = "Info",
                    Passed = true, InstanceName = "SYN-SQL2", Message = "OK" },
        };

        // ── Pin 1: HEADLINE — headless QuestPDF render, no WebView2/UI-thread dependency ──────────

        [Fact]
        public void Report_BuildsBriefing_FromResults()
        {
            var briefing = ScheduledReportService.BuildBriefing(Meta(), SampleResults());
            Assert.Equal(4, briefing.Findings.Count);
            // Precedence mirrors the interactive briefing: 1 pass, 1 INFO, 2 fail.
            //
            // reports-r2-03 (2026-08-27): this used to assert 2 passes, and the second of them was
            // the "SA account renamed" row whose declared Severity is "Info". That assertion pinned
            // the defect — this ladder had no Info arm, so an informational note exported as a green
            // Pass and rode the pass rate in an unattended client-facing PDF. The interactive
            // QuickCheck ladder and the CLI's CsvResultWriter had both carried the Info arm since
            // 2026-07-16; this was the copy that never got it.
            Assert.Equal(1, briefing.Findings.Count(f => f.State == FindingState.Pass));
            Assert.Equal(1, briefing.Findings.Count(f => f.State == FindingState.Info));
            Assert.Equal(2, briefing.Findings.Count(f => f.State == FindingState.Fail));
        }

        // ── reports-r2-03: the unattended ladder was the 4th copy of one rule, missing two arms ──
        //
        // Measured before the fix, driving the real BuildBriefing: a Verdict-INFO result mapped to
        // FindingState.Pass and the PDF read "75% Passed, 3 of 4 checks passed"; a Verdict-SKIP row
        // did the same, because this file's private skip test only looked for a "SKIP" Message
        // prefix and a verdict-contract check's Message comes from its own template
        // (CheckExecutionService) — the exact corpus shape CheckClassification.IsSkip was hardened
        // against. Nobody reviews this artifact before it emails out.
        [Fact]
        public void BuildBriefing_DoesNotExportAVerdictInfoOrVerdictSkipCheckAsAPass()
        {
            var results = new List<CheckResult>
            {
                // Declared Severity is High; the VERDICT is what makes it informational.
                new() { CheckId = "INFO1", CheckName = "MAXDOP recommendation", Category = "Performance",
                        Severity = "High", Passed = true, Verdict = "INFO", InstanceName = "SYN-SQL1" },
                // The SQLT-CORE-00580 shape: Verdict SKIP, no "SKIP" prefix on the message.
                new() { CheckId = "SKIP1", CheckName = "AG health", Category = "Availability",
                        Severity = "High", Passed = true, Verdict = "SKIP", Message = "No AGs configured.",
                        InstanceName = "SYN-SQL1" },
                new() { CheckId = "PASS1", CheckName = "Backups current", Category = "Reliability",
                        Severity = "High", Passed = true, Verdict = "PASS", Message = "OK",
                        InstanceName = "SYN-SQL1" },
            };

            var briefing = ScheduledReportService.BuildBriefing(Meta(), results);

            Assert.Equal(FindingState.Info,    briefing.Findings[0].State);
            Assert.Equal(FindingState.Skipped, briefing.Findings[1].State);
            Assert.Equal(FindingState.Pass,    briefing.Findings[2].State);
            // One genuine pass in the artifact, not three.
            Assert.Equal(1, briefing.Findings.Count(f => f.State == FindingState.Pass));
        }

        // ── The arm the consolidation dropped (fix round, 2026-08-27) ────────────────────────────
        //
        // The private IsSkipped this ladder deleted did Message.TrimStart() before testing the
        // prefix; the shared CheckClassification.IsSkip did not. So consolidating onto the shared
        // rule silently narrowed this copy: measured by driving the real ToFindingRow, a result whose
        // Message reads "  SKIP - x" came back Skipped before the swap and Pass after it — a green
        // pass on the rate, in the unattended PDF, for a check that did not run. Zero corpus checks
        // emit a whitespace-led SKIP today, so this is a latent arm, restored in the shared rule
        // (which fixes all four copies rather than re-forking this one).
        [Fact]
        public void BuildBriefing_TreatsAWhitespaceLedSkipMessageAsASkip_NotAPass()
        {
            var results = new List<CheckResult>
            {
                new() { CheckId = "WSSKIP1", CheckName = "Resource governor", Category = "Performance",
                        Severity = "Low", Passed = true, Message = "  SKIP - not applicable on this edition",
                        InstanceName = "SYN-SQL1" },
            };

            var briefing = ScheduledReportService.BuildBriefing(Meta(), results);

            Assert.Equal(FindingState.Skipped, briefing.Findings[0].State);
            Assert.Empty(briefing.Findings.Where(f => f.State == FindingState.Pass));
        }

        // Report_Renders_ValidPdf_OnBackgroundThread_NoWebView2, Report_Renders_EvenWithNoPosture and
        // Report_SaveToReportsFolder_WritesUnderOutputReports moved 2026-08-04 to
        // Gated/ScheduledReportRenderTests.cs. They assert that Render PRODUCES a PDF, which is only
        // true in an edition that ships the Executive Briefing. Meta()/SampleResults() stay here and
        // are now internal so the moved tests reuse them.

        /// <summary>
        /// The gate itself, exercised rather than merely stepped around — and the only test in this
        /// file that runs in BOTH editions.
        ///
        /// <para>ScheduledReportService.Render is the third kind of profile gate in this codebase,
        /// and the one no compiler and no source scan can catch: the method compiles everywhere and
        /// refuses at RUNTIME when reports."executive-briefing" is off. Whichever side of the fence
        /// this build is on, Render must agree with BuildModules.Reports.ExecutiveBriefing — the
        /// same const the UI uses to decide whether to offer the button. A build that renders while
        /// claiming not to ship the report, or claims to ship it and throws, is broken either way.</para>
        /// </summary>
        [Fact]
        public void Render_MatchesThisEditionsBriefingGate()
        {
            var briefing = ScheduledReportService.BuildBriefing(Meta(), SampleResults());

            if (BuildModules.Reports.ExecutiveBriefing)
            {
                var pdf = ScheduledReportService.Render(ReportKind.ExecutiveBriefing, briefing);
                Assert.True(pdf.Length > 1000,
                    $"this edition reports the Executive Briefing as included, but Render produced {pdf.Length} bytes.");
            }
            else
            {
                var thrown = Assert.Throws<NotSupportedException>(
                    () => ScheduledReportService.Render(ReportKind.ExecutiveBriefing, briefing));
                Assert.Contains("not included in this edition", thrown.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── Pin 2: email attaches the file in BOTH SMTP and Graph shapes ─────────────────────────

        private static (string path, byte[] bytes) TempPdf()
        {
            var bytes = new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', 1, 2, 3, 4 };
            var path = Path.Combine(Path.GetTempPath(), $"msp5-report-{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(path, bytes);
            return (path, bytes);
        }

        [Fact]
        public void EmailReport_SmtpShape_AttachesFile()
        {
            var (path, _) = TempPdf();
            try
            {
                var smtp = new SmtpChannelConfig { FromAddress = "reports@synthetic.local", FromName = "SQLTriage" };
                var msg = NotificationChannelService.BuildReportEmail(
                    smtp, new[] { "cto@synthetic.local" }, "Estate Report", "<p>Attached.</p>", path);

                var attachments = msg.Attachments.OfType<MimePart>().ToList();
                Assert.Single(attachments);
                Assert.Equal(Path.GetFileName(path), attachments[0].FileName);
                Assert.Contains(msg.To.Mailboxes, m => m.Address == "cto@synthetic.local");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void EmailReport_GraphShape_AttachesBase64File()
        {
            var (path, bytes) = TempPdf();
            try
            {
                var smtp = new SmtpChannelConfig { FromAddress = "reports@synthetic.local", FromName = "SQLTriage" };
                var json = NotificationChannelService.BuildGraphReportJson(
                    smtp, new[] { "cto@synthetic.local" }, "Estate Report", "<p>Attached.</p>", path);

                using var doc = JsonDocument.Parse(json);
                var attachments = doc.RootElement.GetProperty("message").GetProperty("attachments");
                Assert.Equal(1, attachments.GetArrayLength());
                var att = attachments[0];
                Assert.Equal("#microsoft.graph.fileAttachment", att.GetProperty("@odata.type").GetString());
                Assert.Equal(Path.GetFileName(path), att.GetProperty("name").GetString());
                var contentBytes = Convert.FromBase64String(att.GetProperty("contentBytes").GetString()!);
                Assert.Equal(bytes, contentBytes);

                var to = doc.RootElement.GetProperty("message").GetProperty("toRecipients");
                Assert.Equal("cto@synthetic.local",
                    to[0].GetProperty("emailAddress").GetProperty("address").GetString());
            }
            finally { File.Delete(path); }
        }

        // ── Pin 3: cadence due-check fires only on schedule ──────────────────────────────────────

        private static ScheduledTaskDefinition DailyReportTask(string time = "02:00") => new()
        {
            Name = "Nightly Estate Report",
            TaskType = TaskType.Report,
            Schedule = new TaskSchedule { Type = ScheduleType.Daily, TimeOfDay = time },
        };

        [Fact]
        public void Cadence_Daily_NotDueBeforeTime_NeverRun()
        {
            var task = DailyReportTask("02:00");
            var now = new DateTime(2026, 7, 9, 1, 0, 0);   // 01:00, before 02:00
            Assert.False(ScheduledTaskEngine.IsDue(task, now, default));
        }

        [Fact]
        public void Cadence_Daily_DueAfterTime_NeverRun()
        {
            var task = DailyReportTask("02:00");
            var now = new DateTime(2026, 7, 9, 3, 0, 0);   // 03:00, after 02:00, never run
            Assert.True(ScheduledTaskEngine.IsDue(task, now, default));
        }

        [Fact]
        public void Cadence_Daily_NotDueTwiceSameDay()
        {
            var task = DailyReportTask("02:00");
            var now = new DateTime(2026, 7, 9, 3, 0, 0);
            var alreadyRanToday = new DateTime(2026, 7, 9, 2, 30, 0);
            Assert.False(ScheduledTaskEngine.IsDue(task, now, alreadyRanToday));
        }

        [Fact]
        public void Cadence_Daily_DueNextDay()
        {
            var task = DailyReportTask("02:00");
            var now = new DateTime(2026, 7, 10, 3, 0, 0);
            var ranYesterday = new DateTime(2026, 7, 9, 2, 30, 0);
            Assert.True(ScheduledTaskEngine.IsDue(task, now, ranYesterday));
        }

        [Fact]
        public void Cadence_Weekly_FiresOnlyOnConfiguredDay()
        {
            var task = new ScheduledTaskDefinition
            {
                TaskType = TaskType.Report,
                Schedule = new TaskSchedule { Type = ScheduleType.Weekly, TimeOfDay = "02:00", DayOfWeek = DayOfWeek.Monday },
            };
            var monday = new DateTime(2026, 7, 6, 3, 0, 0);   // Monday
            var tuesday = new DateTime(2026, 7, 7, 3, 0, 0);  // Tuesday
            Assert.True(ScheduledTaskEngine.IsDue(task, monday, default));
            Assert.False(ScheduledTaskEngine.IsDue(task, tuesday, default));
        }

        // ── Pin 4: opt-in — a fresh install with no created task never runs a report ─────────────

        [Fact]
        public void OptIn_FreshDefinitions_HaveNoReportTask()
        {
            var svc = new ScheduledTaskDefinitionService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ScheduledTaskDefinitionService>.Instance);

            // No operator has created a task → nothing is enabled → the engine iterates nothing.
            Assert.DoesNotContain(svc.GetEnabledTasks(), t => t.TaskType == TaskType.Report);
        }
    }
}
