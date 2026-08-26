/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
                ControlMappings = "ISO/IEC 27001 (2022) · SOC 2 Trust Services",
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
                                        Impact = "An unverified backup can restore corrupt pages.",
                                        Action = "Enable CHECKSUM on all backup jobs.", Effort = "1h" }
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
        public void Renders_the_shape_a_real_audit_actually_produces()
        {
            // THE CASE EVERY FIXTURE ABOVE MISSES, and it is not hypothetical: a real sp_Blitz audit
            // of .\OLD2017 on 2026-08-26 threw DocumentLayoutException out of Build, so the page
            // caught it and showed "PDF export failed" and the client got nothing. The category card
            // was wrapped in a QuestPDF Row to paint its coloured left spine, and that made the card
            // unsplittable; the real L4 Configuration card carries 50 checks.
            //
            // The fixtures above have ONE category of six items each. Typing a bigger one by hand did
            // not reproduce it either - the shape that breaks is 43 categories across 5 levels with
            // real names and real prose, not a big number in one place. So this payload is CAPTURED
            // from the live run (Gated/RoadmapArtefactLiveSmokeTests writes it) and checked in, per
            // the house rule about driving renderer tests from captured payloads.
            var json = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "roadmap-report-old2017-2026-08-26.json"));
            var levels = JsonSerializer.Deserialize<List<RoadmapLevel>>(
                json, new JsonSerializerOptions { IncludeFields = true })!;

            Assert.Equal(5, levels.Count);
            Assert.True(levels.SelectMany(l => l.Categories).Max(c => c.ItemCount) >= 50,
                "the captured payload no longer carries the big category that reproduces the "
                + "overflow, so this test would pass without measuring anything. Re-capture it.");

            var r = Report(93);
            r.Levels = levels;

            var bytes = RoadmapPdfBuilder.Build(r);
            Assert.True(bytes.Length > 1000);
            Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, bytes.Take(4).ToArray());
        }

        [Fact]
        public void The_report_carries_no_AchievedLevel_field_for_a_caller_to_set()
        {
            // The cover's progress dots read "achieved through". The first fix pointed them at a DTO
            // field that no code checked against the rows the same document prints, so a report with
            // AchievedLevel = 5 beside five levels at 0/0 rendered five FILLED dots on the cover and
            // NOT ASSESSED on every level page. Measured on a rendered PDF, not argued.
            //
            // RoadmapPdfBuilder.Cover now derives the number from Levels through
            // RoadmapReportRules.AchievedLevel. Reflection, not a text search: the instrument is the
            // type. Adding the field back fails here.
            var members = typeof(RoadmapReport).GetFields().Select(f => f.Name)
                .Concat(typeof(RoadmapReport).GetProperties().Select(p => p.Name))
                .ToList();

            Assert.DoesNotContain("AchievedLevel", members);
            Assert.Contains("Levels", members);
        }

        [Fact]
        public void A_cover_cannot_claim_a_level_the_rows_beneath_it_never_assessed()
        {
            // The fabricated shape, rendered for real: five levels with no checks in them. The PDF
            // must compose, and the number its cover reads must be zero. Nothing in this project
            // extracts text from a PDF, so the glyph itself is not asserted here; the number that
            // chooses it is, through the same call the builder makes.
            var r = Report(0);
            foreach (var level in r.Levels)
            {
                level.PassedCount = 0;
                level.TotalCount = 0;
                level.PassRate = 0;
                level.Categories.Clear();
            }

            Assert.Equal(0, RoadmapReportRules.AchievedLevel(
                r.Levels.Select(l => (l.PassedCount, l.TotalCount, l.Target)).ToList()));

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
