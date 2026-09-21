/* In the name of God, the Merciful, the Compassionate */

// THE LIVE PROOF FOR LANE 5's ARTEFACT CLUSTERS (B, C, E, F). Everything in
// RoadmapArtefactHonestyTests is a unit fact or a text tripwire. This file is the only thing that
// puts a REAL sp_Blitz run against a REAL instance through the real runner, the real CSV writer and
// the real AuditOutputScanner, and then renders the real client PDF and the real Action Plan CSV
// from what came back, so the two artefacts can be read side by side.
//
// WHAT IT PROVES WHEN ARMED
//   1. audit-r1-01 live: two exports of one script against one server IN ONE PROCESS produce TWO
//      files. Before the fix the file name carried a stamp captured when the runner was
//      CONSTRUCTED, so both exports resolved to one path and the second destroyed the first with no
//      log line and no warning.
//   2. audit-r1-05 live: the SECOND run finds a fresh master.dbo.sqldba_sp_Blitz_output, the shipped
//      ExecutionTest returns ToRun=0, no EXEC happens, and the result now SAYS SO through
//      ReusedPreviousExecution. That flag is what the corrected log line and the post-run modal
//      read. The first run, against a dropped table, must not raise it.
//   3. audit-r1-09 / audit-r2-05 / audit-r2-08 live: the client PDF and the Action Plan CSV are
//      built from ONE set of numbers, and every level's status string, the achieved level behind the
//      cover dots, and the severity banding are read from RoadmapReportRules by both.
//   4. The PDF composes without throwing on REAL data. The offline render tests feed it synthetic
//      levels; a live audit produces category cards and remediation prose of real lengths, and a
//      QuestPDF layout failure surfaces at export time in front of the customer.
//
// WHAT IT DOES NOT PROVE, said plainly. The projection from the audit into a RoadmapReport is
// reconstructed here out of RoadmapReportRules, CheckUniverse and the shipped roadmap-mapping.json.
// It is not Pages/DiagnosticsRoadmap.razor's own aggregation loop, which lives inside a Blazor
// component nothing in this project renders. So this file proves the RULES agree across the two
// artefacts and that both artefacts build from real audit data. That the razor calls those same
// rules is held by the text tripwires in RoadmapArtefactHonestyTests, which is a weaker instrument
// and is labelled as one there.
//
// INERT unless armed, the same shape as FrkLiveSmokeTests: LiveFactAttribute reports SKIPPED when
// FRK_LIVE_TARGET is unset, and RequireTarget fails the body rather than passing vacuously if that
// attribute is ever weakened.
//
// INVOCATION (first armed run 2026-08-26 against .\OLD2017):
//   $env:FRK_LIVE_TARGET = ".\OLD2017"
//   $env:FRK_LIVE_EVIDENCE_DIR = "C:\temp\audit-lane5-artefacts"
//   dotnet test SQLTriage.sln -c Debug --no-build --filter "FullyQualifiedName~RoadmapArtefactLiveSmokeTests"
//
// FIXTURE CONTRACT. FRK_LIVE_TARGET must name an instance the caller is happy to have sp_ineachdb
// and sp_Blitz installed into master on, because that is what the app does to a client server on
// every run. The test DROPS master.dbo.sqldba_sp_Blitz_output first, so run one is a real EXEC and
// run two is a real ToRun=0 skip. It plants no databases and creates nothing else.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public class RoadmapArtefactLiveSmokeTests
{
    private readonly ITestOutputHelper _out;
    public RoadmapArtefactLiveSmokeTests(ITestOutputHelper output) => _out = output;
    private void Line(string s) => _out.WriteLine(s);

    private static string? Target => Environment.GetEnvironmentVariable("FRK_LIVE_TARGET");
    private static string? EvidenceDir => Environment.GetEnvironmentVariable("FRK_LIVE_EVIDENCE_DIR");

    private static string RequireTarget()
    {
        Assert.False(string.IsNullOrWhiteSpace(Target),
            "FRK_LIVE_TARGET is not set, so this test has no instance to audit and nothing to "
            + "assert. It should have been SKIPPED by LiveFactAttribute; if it ran, that attribute "
            + "is no longer doing its job.");
        return Target!;
    }

    private static string ConnString(string target) =>
        new SqlConnectionStringBuilder
        {
            DataSource = target,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            ApplicationName = "SQLTriage.Tests.RoadmapArtefactLiveSmoke",
        }.ConnectionString;

    private static void Exec(string target, string sql)
    {
        using var conn = new SqlConnection(ConnString(target));
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.ExecuteNonQuery();
    }

    private static string EvidencePath(string fileName)
    {
        var dir = EvidenceDir;
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "roadmap-artefacts");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    [LiveFact("FRK_LIVE_TARGET")]
    public async Task A_real_audit_produces_a_pdf_and_a_csv_that_agree()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var target = RequireTarget();
        Line("target        : " + target);

        // ── Fixture reset so run one is a real EXEC ────────────────────────────────────────
        Exec(target, "IF OBJECT_ID('master.dbo.sqldba_sp_Blitz_output') IS NOT NULL "
                     + "DROP TABLE master.dbo.sqldba_sp_Blitz_output;");

        var runner = new DiagnosticScriptRunner(
            new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
            NullLogger<DiagnosticScriptRunner>.Instance);
        var connection = new ServerConnection
        {
            ServerNames = target,
            Database = "master",
            UseWindowsAuthentication = true,
            TrustServerCertificate = true,
        };

        var configs = runner.LoadScriptConfigurations();
        var ineachdb = configs.Single(c => c.ScriptPath == "sp_ineachdb.sql");
        var blitz = configs.Single(c => c.ScriptPath == "sp_Blitz.sql");

        var prereq = await runner.ExecuteScriptAsync(ineachdb, connection, target);
        Assert.True(prereq.Success, "installing sp_ineachdb failed: " + (prereq.ErrorMessage ?? "no message"));

        var outputDir = Path.Combine(AppContext.BaseDirectory, "output");
        var before = Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir, "*_sp_Blitz_*.csv").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Run one: a real execution ─────────────────────────────────────────────────────
        var first = await runner.ExecuteScriptAsync(blitz, connection, target);
        Assert.True(first.Success, "sp_Blitz run one failed: " + (first.ErrorMessage ?? "no message"));
        Assert.False(first.ReusedPreviousExecution,
            "the output table was dropped, so ExecutionTest must have returned ToRun=1 and this run "
            + "must have EXECed. If it says it reused a previous execution the fixture reset failed "
            + "and nothing below is measuring a real run");
        Line("run one       : rows=" + first.RowsAffected + " reused=" + first.ReusedPreviousExecution);

        // ── Run two, SAME PROCESS: the ToRun=0 skip, and the export-name collision ─────────
        var second = await runner.ExecuteScriptAsync(blitz, connection, target);
        Assert.True(second.Success, "sp_Blitz run two failed: " + (second.ErrorMessage ?? "no message"));
        Assert.True(second.ReusedPreviousExecution,
            "run one just wrote a fresh master.dbo.sqldba_sp_Blitz_output, so the shipped "
            + "ExecutionTest returns ToRun=0 and run two exports the previous execution's rows. "
            + "That path used to log 'executed successfully' and raise nothing in the post-run modal");
        Assert.Equal("✓ Loaded previous execution", second.StatusMessage);
        Line("run two       : rows=" + second.RowsAffected + " reused=" + second.ReusedPreviousExecution
             + " status=" + second.StatusMessage);

        var after = Directory.GetFiles(outputDir, "*_sp_Blitz_*.csv")
            .Where(f => !before.Contains(f)).OrderBy(f => f).ToList();
        Line("new csv files : " + after.Count);
        foreach (var f in after) Line("  " + Path.GetFileName(f) + "  " + new FileInfo(f).Length + " bytes");
        Assert.True(after.Count == 2,
            "two exports in one process wrote " + after.Count + " file(s). Before this lane the "
            + "stamp was captured when the runner was CONSTRUCTED, so both resolved to one path and "
            + "run one was silently destroyed");
        Assert.True(after.All(f => new FileInfo(f).Length > 0), "an export wrote an empty file");

        // ── The real scanner over the real file ───────────────────────────────────────────
        var scanDir = Path.Combine(Path.GetTempPath(), "roadmap-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scanDir);
        try
        {
            var newest = after.Last();
            File.Copy(newest, Path.Combine(scanDir, Path.GetFileName(newest)));

            var scanner = new AuditOutputScanner(NullLogger<AuditOutputScanner>.Instance, scanDir);
            var scanned = await scanner.ScanAsync();
            Assert.Empty(scanned.Unreadable);
            var file = Assert.Single(scanned.Files);
            await scanner.LoadFiredChecksAsync(file);

            Assert.Equal(AuditParseOutcome.Parsed, file.ParseOutcome);
            Assert.True(file.FiredCheckCounts.Count > 0, "the scanner parsed no fired CheckIDs");
            Line("fired ids     : " + file.FiredCheckCounts.Count + " distinct");

            // ── The projection (reconstructed - see the file header) ──────────────────────
            var mapJson = File.ReadAllText(FrkContractTests.ConfigPath("roadmap-mapping.json"));
            var levels = BuildLevels(mapJson, file.FiredCheckCounts.Keys.ToHashSet());
            var achieved = RoadmapReportRules.AchievedLevel(
                levels.Select(l => (l.PassedCount, l.TotalCount, l.Target)).ToList());
            var stage = RoadmapReportRules.CurrentStage(achieved);

            Line("achieved      : L" + achieved + "   stage: L" + stage);
            foreach (var l in levels)
                Line("  L" + l.Number + " " + l.PassedCount + "/" + l.TotalCount
                     + "  " + l.PassRate.ToString("N1", CultureInfo.InvariantCulture) + "%  "
                     + RoadmapReportRules.LevelStatus(l.PassedCount, l.TotalCount, l.Target));

            Assert.True(levels.Sum(l => l.TotalCount) > 100,
                "the scoring universe collapsed to " + levels.Sum(l => l.TotalCount) + " checks");

            // A level the cover paints as achieved must be one this run measured at or above its
            // target. That is the whole of audit-r2-05, checked against live numbers.
            for (var n = 1; n <= achieved; n++)
                Assert.True(RoadmapReportRules.TargetMet(levels[n - 1].PassedCount, levels[n - 1].TotalCount),
                    "L" + n + " is inside the achieved level but did not meet its target");
            if (achieved < 5)
                Assert.False(RoadmapReportRules.TargetMet(levels[achieved].PassedCount, levels[achieved].TotalCount),
                    "L" + (achieved + 1) + " met its target, so the ladder should have climbed past it");

            // ── The two artefacts, from those numbers ─────────────────────────────────────
            var report = new RoadmapReport
            {
                ServerLabel = target,
                GeneratedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mmZ", CultureInfo.InvariantCulture),
                TimezoneId = TimeZoneInfo.Local.Id,
                RunId = "live0001",
                CoverSubtitle = target,
                PreparedForDate = DateTime.UtcNow.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture),
                OverallLevel = stage,
                OverallLevelName = "Live",
                OverallScore = levels.Sum(l => l.TotalCount) > 0
                    ? levels.Sum(l => l.PassedCount) * 100.0 / levels.Sum(l => l.TotalCount) : 0,
                RiskWeightedScore = 0,
                FindingsEvaluated = levels.Sum(l => l.TotalCount),
                FindingsPassed = levels.Sum(l => l.PassedCount),
                ServersSelected = 1,
                Headline = "Live probe against " + target,
                Critical = 0, High = 0, Medium = 0,
                RunIdFull = "live-" + Guid.NewGuid().ToString("D"),
                GeneratedUtcIso = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ToolVersion = "live",
                Operator = "live probe",
                ControlMappings = "ISO/IEC 27001 (2022) · SOC 2 Trust Services · NIST CSF 2.0 · CIS Controls v8",
                ServersInScope = new List<string> { target },
                Levels = levels,
            };

            // audit-r2-06, on real data: the two mapping fields land in two different slots, and a
            // next_action still carrying an unresolved <Owner> token prints nothing rather than
            // shipping template scaffolding to a client.
            var failed = levels.SelectMany(l => l.Categories).SelectMany(c => c.Failed).ToList();
            Line("failed cards  : " + failed.Count
                 + "  with impact: " + failed.Count(f => !string.IsNullOrWhiteSpace(f.Impact))
                 + "  with action: " + failed.Count(f => !string.IsNullOrWhiteSpace(f.Action)));
            Assert.NotEmpty(failed);
            Assert.Contains(failed, f => !string.IsNullOrWhiteSpace(f.Impact));
            Assert.All(failed, f => Assert.False(
                RoadmapReportRules.HasUnresolvedPlaceholder(f.Action),
                "a per-check action reached the client PDF with a template placeholder in it"));

            // The captured payload. RoadmapPdfBuilder is a composition, not a template, and its
            // layout failures depend on the SHAPE of real data - how many checks land in one
            // category, how long the real prose is. Every synthetic fixture in
            // Gated/RoadmapPdfBuilderTests has one category of six items, and a real audit threw
            // DocumentLayoutException on a card the synthetic ones never approach. So the shape is
            // captured here and checked in, per the house rule: drive renderer tests from captured
            // payloads, not from typed ones.
            File.WriteAllText(EvidencePath("roadmap-report-capture.json"),
                JsonSerializer.Serialize(report.Levels, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }),
                new UTF8Encoding(false));

            var pdfBytes = RoadmapPdfBuilder.Build(report);
            var pdfPath = EvidencePath("RoadmapReport_live.pdf");
            File.WriteAllBytes(pdfPath, pdfBytes);
            Line("pdf           : " + pdfPath + "  " + pdfBytes.Length + " bytes");
            Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, pdfBytes.Take(4).ToArray());

            // The Action Plan CSV's summary block, by the same rule the page's ExportCsv uses.
            var csv = new StringBuilder();
            csv.AppendLine("Level,Stage,Pass Rate,Passed,Total,Target,Status");
            foreach (var l in levels)
                csv.AppendLine(string.Join(",", "L" + l.Number, l.Name,
                    l.PassRate.ToString("N0", CultureInfo.InvariantCulture) + "%",
                    l.PassedCount, l.TotalCount, l.Target + "%",
                    RoadmapReportRules.LevelStatus(l.PassedCount, l.TotalCount, l.Target)));
            var csvPath = EvidencePath("RoadmapActionPlan_live.csv");
            File.WriteAllText(csvPath, csv.ToString(), new UTF8Encoding(false));
            Line("csv           : " + csvPath);

            // What the PDF was handed, so a reader outside this process can check the rendered
            // pages against it rather than against a claim made here.
            File.WriteAllText(EvidencePath("expected-artefact-numbers.json"),
                JsonSerializer.Serialize(new
                {
                    instance = target,
                    achievedLevel = achieved,
                    currentStage = stage,
                    overallScore = report.OverallScore,
                    severityBandCaption = RoadmapReportRules.SeverityBandCaption,
                    levels = levels.Select(l => new
                    {
                        level = l.Number,
                        passed = l.PassedCount,
                        total = l.TotalCount,
                        passRate = Math.Round(l.PassRate, 1),
                        target = l.Target,
                        status = RoadmapReportRules.LevelStatus(l.PassedCount, l.TotalCount, l.Target),
                        coverDotFilled = l.Number <= achieved,
                    }),
                }, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        finally
        {
            try { Directory.Delete(scanDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The scoring universe from the shipped mapping, through the SAME filters the page applies:
    /// non-informational category, level 1 to 5, and CheckUniverse.CanFire. Fired-and-bad is a
    /// fail; fired-and-informational and not-fired are both passes. Failed findings carry the two
    /// separate mapping fields into the two separate slots, which is audit-r2-06's whole subject.
    /// </summary>
    private static List<RoadmapLevel> BuildLevels(string mapJson, HashSet<int> firedIds)
    {
        var perLevel = Enumerable.Range(1, 5)
            .ToDictionary(n => n, _ => new Dictionary<string, RoadmapCategory>(StringComparer.OrdinalIgnoreCase));
        var counts = Enumerable.Range(1, 5).ToDictionary(n => n, _ => (Passed: 0, Total: 0));

        using var doc = JsonDocument.Parse(mapJson);
        foreach (var entry in doc.RootElement.GetProperty("blitzCheckMap").EnumerateArray())
        {
            var checkId = entry.TryGetProperty("checkId", out var ci) ? ci.GetInt32() : -1;
            var level = entry.TryGetProperty("level", out var lv) ? lv.GetInt32() : 0;
            var category = entry.TryGetProperty("category", out var cv) ? cv.GetString() ?? "" : "";
            var name = entry.TryGetProperty("findingName", out var fn) ? fn.GetString() ?? "" : "";
            var isBad = !entry.TryGetProperty("IsBad", out var ib) || ib.GetInt32() != 0;

            if (category.Equals("Server Info", StringComparison.OrdinalIgnoreCase) ||
                category.Equals("Information", StringComparison.OrdinalIgnoreCase) ||
                category.Equals("Informational", StringComparison.OrdinalIgnoreCase)) continue;
            if (!CheckUniverse.CanFire(checkId)) continue;
            if (level < 1 || level > 5) continue;

            var roadmap = entry.TryGetProperty("Roadmap", out var rm) ? rm.GetString() ?? category : category;
            var cats = perLevel[level];
            if (!cats.TryGetValue(roadmap, out var cat))
                cats[roadmap] = cat = new RoadmapCategory { Name = roadmap };

            var (passed, total) = counts[level];
            total++;
            cat.TotalCount++;

            var fired = firedIds.Contains(checkId);
            if (fired && isBad)
            {
                cat.Failed.Add(new RoadmapFinding
                {
                    Name = name,
                    Tag = category,
                    Impact = Str(entry, "business_translation"),
                    Action = RoadmapReportRules.PerCheckAction(Str(entry, "next_action")),
                    Effort = Str(entry, "effort_estimate"),
                });
            }
            else
            {
                passed++; cat.PassedCount++;
                if (fired) cat.Info.Add(new RoadmapFinding { Name = name, Tag = category });
                else cat.PassedItems.Add(new RoadmapFinding { Name = name, Tag = category });
            }
            counts[level] = (passed, total);
        }

        return Enumerable.Range(1, 5).Select(n => new RoadmapLevel
        {
            Number = n,
            Name = "Level " + n,
            Description = "Live probe",
            PassedCount = counts[n].Passed,
            TotalCount = counts[n].Total,
            PassRate = RoadmapReportRules.PassRate(counts[n].Passed, counts[n].Total),
            Target = RoadmapReportRules.DefaultTargetPercent,
            Categories = perLevel[n].Values.OrderByDescending(c => c.TotalCount).ToList(),
        }).ToList();
    }

    private static string? Str(JsonElement entry, string property)
        => entry.TryGetProperty(property, out var v) && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;
}
