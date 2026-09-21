/* In the name of God, the Merciful, the Compassionate */

// Extracted from ScheduledReportTests.cs on 2026-08-04 — a THIRD gating mechanism, and the one
// neither the compiler nor a source lint can see.
//
// ScheduledReportService.Render binds nothing gated and compiles in every profile. It refuses at
// RUNTIME: under !SQLT_NO_REPORT_EXEC_BRIEFING it renders, and under the community profile
// (buildprofile.json reports."executive-briefing" = "off") its whole body is replaced by
//     throw new NotSupportedException("Executive report generation is not included in this edition.")
// So these three tests compiled perfectly in the community build and then FAILED when run — three
// of the seven failures the community suite produced on 2026-08-04, the first time CI had ever got
// past BUILD to run it at all.
//
// They assert full-edition BEHAVIOUR, so they live here. The gate itself is not merely avoided:
// ScheduledReportTests.Render_MatchesThisEditionsBriefingGate exercises it in BOTH editions.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests.Gated
{
    public class ScheduledReportRenderTests
    {
        static ScheduledReportRenderTests()
        {
            // App.xaml.cs sets this at startup; tests bypass startup, so set it here.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        [Fact]
        public void Report_Renders_ValidPdf_OnBackgroundThread_NoWebView2()
        {
            var briefing = ScheduledReportService.BuildBriefing(
                ScheduledReportTests.Meta(), ScheduledReportTests.SampleResults());

            byte[]? pdf = null;
            Exception? failure = null;
            bool hadSyncContext = true;
            bool wasBackground = false;

            // A CoreWebView2 PrintToPdfAsync needs an STA thread with a running message pump; running the
            // renderer on a bare MTA background thread with NO SynchronizationContext proves this path is
            // pure QuestPDF and safe for the background ScheduledTaskEngine.
            var t = new Thread(() =>
            {
                try
                {
                    hadSyncContext = SynchronizationContext.Current != null;
                    wasBackground = Thread.CurrentThread.IsBackground;
                    pdf = ScheduledReportService.Render(ReportKind.ExecutiveBriefing, briefing);
                }
                catch (Exception ex) { failure = ex; }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(30)), "Headless render did not complete — a UI-thread dependency would hang here.");

            Assert.Null(failure);
            Assert.False(hadSyncContext, "Render ran with a SynchronizationContext — it must not need a UI pump.");
            Assert.True(wasBackground);
            Assert.NotNull(pdf);
            Assert.True(pdf!.Length > 1000, $"PDF unexpectedly small: {pdf.Length} bytes");
            Assert.Equal((byte)'%', pdf[0]);
            Assert.Equal((byte)'P', pdf[1]);
            Assert.Equal((byte)'D', pdf[2]);
            Assert.Equal((byte)'F', pdf[3]);
        }

        [Fact]
        public void Report_Renders_EvenWithNoPosture()
        {
            // A fresh estate with no assessment yet still produces a valid header+donut PDF (never throws).
            var briefing = ScheduledReportService.BuildBriefing(ScheduledReportTests.Meta(), new List<CheckResult>());
            var pdf = ScheduledReportService.Render(ReportKind.ExecutiveBriefing, briefing);
            Assert.True(pdf.Length > 1000);
        }

        [Fact]
        public void Report_SaveToReportsFolder_WritesUnderOutputReports()
        {
            var briefing = ScheduledReportService.BuildBriefing(
                ScheduledReportTests.Meta(), ScheduledReportTests.SampleResults());
            var pdf = ScheduledReportService.Render(ReportKind.ExecutiveBriefing, briefing);
            var path = ScheduledReportService.SaveToReportsFolder(pdf, "2 servers", DateTime.UtcNow, "synth001");
            try
            {
                Assert.True(File.Exists(path));
                var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "output", "reports"));
                Assert.StartsWith(dir, Path.GetFullPath(path));
                Assert.EndsWith(".pdf", path);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
