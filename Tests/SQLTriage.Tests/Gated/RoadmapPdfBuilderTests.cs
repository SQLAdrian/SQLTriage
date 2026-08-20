/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The roadmap PDF is composed, not templated: a layout mistake surfaces as a runtime
    /// DocumentLayoutException at export time, in front of the customer, not at build time.
    /// These tests actually RENDER the document so that failure lands here instead.
    ///
    /// The 0%% and 100%% cases matter more than they look: the pass-rate gauge is drawn as two
    /// weighted cells, and QuestPDF reads a zero relative weight as "unconstrained" — so a
    /// naive implementation paints a 0%% score as a FULL bar. Rendering both ends pins the guard.
    /// </summary>
    public class RoadmapPdfBuilderTests
    {
        static RoadmapPdfBuilderTests()
        {
            // The hosts (App.xaml.cs, CliAuditHost, WindowsServiceHost) set this at startup;
            // the test host has no startup, so do it here or every render throws on licensing.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        private static RoadmapReport Report(double passRate, bool colorBlind = false, bool watermark = false)
        {
            var total = 10;
            var passed = (int)System.Math.Round(passRate / 100.0 * total);
            return new RoadmapReport
            {
                ServerLabel = "SQL01",
                GeneratedUtc = "2026-07-29T10:00Z",
                RunId = "abc123",
                ColorBlind = colorBlind,
                Watermark = watermark,
                Company = "Contoso",
                CoverSubtitle = "Diagnostics Maturity Roadmap",
                PreparedForDate = "29 July 2026",
                OverallLevel = 3,
                OverallLevelName = "Performant",
                OverallScore = passRate,
                RiskWeightedScore = passRate,
                ItemsImpacted = total - passed,
                FindingsEvaluated = total,
                FindingsPassed = passed,
                ServersSelected = 1,
                Headline = "Backups are not verified on two databases.",
                Critical = 1, High = 2, Medium = 3,
                TopActions = new List<string> { "Enable backup checksums", "Set MAXDOP", "Add an operator" },
                RunIdFull = "abc123-full",
                GeneratedUtcIso = "2026-07-29T10:00:00Z",
                ToolVersion = "0.97.0",
                Operator = "adrian",
                FrameworkVersion = "corpus-v2",
                ServersInScope = new List<string> { "SQL01" },
                Levels = Enumerable.Range(1, 5).Select(n => new RoadmapLevel
                {
                    Number = n,
                    Name = $"Level {n}",
                    Description = "Description",
                    PassedCount = passed,
                    TotalCount = total,
                    PassRate = passRate,
                    Target = 80,
                    Categories = new List<RoadmapCategory>
                    {
                        new()
                        {
                            Name = "Backups",
                            PassedCount = passed,
                            TotalCount = total,
                            Failed = new List<RoadmapFinding>
                            {
                                new() { Name = "Backup checksum off", Tag = "HIGH",
                                        Remediation = "Enable CHECKSUM on all backup jobs.", Effort = "1h" }
                            },
                            Info = new List<RoadmapFinding> { new() { Name = "Informational", Tag = "INFO" } },
                            PassedItems = new List<RoadmapFinding>
                            {
                                new() { Name = "Full backup recent", Tag = "PASS" },
                                new() { Name = "Log backup recent", Tag = "PASS" },
                                new() { Name = "Retention set", Tag = "PASS" },
                                new() { Name = "Offsite copy", Tag = "PASS" }
                            }
                        }
                    }
                }).ToList()
            };
        }

        [Theory]
        [InlineData(0)]     // the dangerous end: a zero score must not paint a full gauge
        [InlineData(37.5)]
        [InlineData(80)]
        [InlineData(100)]   // the other end: no zero-weight remainder cell
        public void Renders_at_every_pass_rate_without_throwing(double passRate)
        {
            var bytes = RoadmapPdfBuilder.Build(Report(passRate));
            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 1000, $"PDF suspiciously small ({bytes.Length} bytes)");
            // "%PDF" magic — proves we produced a document, not an empty buffer.
            Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, bytes.Take(4).ToArray());
        }

        [Fact]
        public void Renders_in_colour_blind_mode()
        {
            var bytes = RoadmapPdfBuilder.Build(Report(55, colorBlind: true));
            Assert.True(bytes.Length > 1000);
        }

        [Fact]
        public void Renders_with_the_watermark()
        {
            var bytes = RoadmapPdfBuilder.Build(Report(55, watermark: true));
            Assert.True(bytes.Length > 1000);
        }

        [Fact]
        public void Renders_an_empty_report_without_throwing()
        {
            // No levels, no findings — the "ran before any assessment" shape.
            var r = Report(0);
            r.Levels.Clear();
            r.TopActions.Clear();
            var bytes = RoadmapPdfBuilder.Build(r);
            Assert.True(bytes.Length > 500);
        }

        [Fact]
        public void Renders_a_category_with_no_checks_at_all()
        {
            // TotalCount == 0 would be a divide-by-zero for the per-category gauge.
            var r = Report(50);
            r.Levels[0].Categories[0].PassedCount = 0;
            r.Levels[0].Categories[0].TotalCount = 0;
            var bytes = RoadmapPdfBuilder.Build(r);
            Assert.True(bytes.Length > 1000);
        }
    }
}
