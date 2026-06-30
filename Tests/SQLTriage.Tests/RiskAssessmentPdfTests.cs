/* In the name of God, the Merciful, the Compassionate */

// Synthetic-data render test for the Risk Assessment report builder. NO SQL, NO
// client data — fabricated DTOs only. Verifies RiskAssessmentPdf.Build produces a
// valid (non-trivial, %PDF-headered) document and exercises the filter branches.

using System;
using System.Collections.Generic;
using SQLTriage.Data.Services.RiskReport;
using Xunit;

namespace SQLTriage.Tests
{
    public class RiskAssessmentPdfTests
    {
        static RiskAssessmentPdfTests()
        {
            // App.xaml.cs sets this at startup; tests bypass startup, so set it here.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        private static RiskAssessmentReport Sample(RiskReportFilters filters) => new()
        {
            Header = new RiskReportHeader
            {
                Domain = "synthetic.local",
                Customer = "Synthetic Test Co",
                ContractType = "Managed Services",
                LastDomainCheck = "2026-06-18",
                Colour = "Amber",
                ServerCount = 3,
                PassedPercent = 72.0,
            },
            Filters = filters,
            Rows = new List<RiskCheckRow>
            {
                new() { SqlInstance="SYN-SQL1", Category="Security", Bad=1, ImpactDescription="High", ComplexityDescription="Low", FailedItems=4, FailedHigh=2, Weight=10, Hours=3.5, Summary="TDE not enabled", Details="Transparent Data Encryption is off on 2 databases.", OutcomeImage="!", Supported="Yes" },
                new() { SqlInstance="SYN-SQL1", Category="Availability", Bad=1, ImpactDescription="High", ComplexityDescription="Medium", FailedItems=1, FailedHigh=1, Weight=8, Hours=6, Summary="No recent full backup", Details="Last full backup > 7 days.", OutcomeImage="X", Supported="Yes" },
                new() { SqlInstance="SYN-SQL2", Category="Performance", Bad=0, ImpactDescription="Medium", ComplexityDescription="Low", FailedItems=3, FailedMedium=3, Weight=4, Hours=2, Summary="High fragmentation", Details="Several indexes > 30% fragmented.", OutcomeImage="!", Supported="No" },
            },
        };

        private static RiskAssessmentMeta Meta() => new()
        {
            Customer = "Synthetic Test Co",
            GeneratedUtc = "2026-06-18T00:00Z",
            TimezoneId = "NZST",
            RunId = "synth001",
            ColorBlind = false,
        };

        [Fact]
        public void Build_ProducesValidPdf()
        {
            var bytes = RiskAssessmentPdf.Build(Sample(new RiskReportFilters { Detailed = true }), Meta());

            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 1000, $"PDF unexpectedly small: {bytes.Length} bytes");
            // %PDF- magic header
            Assert.Equal((byte)'%', bytes[0]);
            Assert.Equal((byte)'P', bytes[1]);
            Assert.Equal((byte)'D', bytes[2]);
            Assert.Equal((byte)'F', bytes[3]);
        }

        [Fact]
        public void Build_ShowAllChecksFalse_StillRenders()
        {
            // ShowAllChecks=false filters to bad/failing rows; sample has those, so non-empty.
            var bytes = RiskAssessmentPdf.Build(Sample(new RiskReportFilters { ShowAllChecks = false }), Meta());
            Assert.True(bytes.Length > 1000);
        }

        [Fact]
        public void Build_EmptyRows_StillRendersHeaderAndDonut()
        {
            var report = Sample(new RiskReportFilters());
            report.Rows.Clear();
            var bytes = RiskAssessmentPdf.Build(report, Meta());
            Assert.True(bytes.Length > 1000, "Even with no findings the header + donut should render.");
        }
    }
}
