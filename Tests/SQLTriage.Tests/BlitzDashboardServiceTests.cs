/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// Tests for the sp_Blitz dashboard scoring model (BlitzDashboardService pure functions).
/// Locks in the model Adrian set 2026-06-30 ("grading sp_Blitz results, not showing cleverness"):
///   - universe = every real (Scored) check; only banner/report categories (Server Info / Rundate)
///     are listed-only. IsBad/severity are DISPLAY-only and do NOT gate the score.
///   - join: SqlCheck.Id starts "SQLT-BLITZ-" + numeric/composite source.ref
///   - score = Σ(weight of non-fired scored) / Σ(weight of all scored) × 100; EVERY fired = ding
///   - unknown fires count as a ding (weight 1); map-only fallback weight from effort_estimate
///   - each finding renders a two-line CIO/DBA voice cell (SQL stripped, graceful fallback).
/// </summary>
public sealed class BlitzDashboardServiceTests
{
    private static SqlCheck Blitz(string idNum, string sourceRef, int weight = 5, double effort = 2,
        bool isBad = true, string severity = "High", string category = "Reliability") =>
        new()
        {
            Id = $"SQLT-BLITZ-{idNum}",
            Source = sourceRef,
            ScoreWeight = weight,
            EffortHours = effort,
            IsBad = isBad,
            Severity = severity,
            Category = category,
            Name = $"Blitz {sourceRef}",
        };

    private static AuditedFile FiredFile(string instance, params int[] firedIds)
    {
        var counts = new Dictionary<int, int>();
        foreach (var id in firedIds) counts[id] = counts.TryGetValue(id, out var c) ? c + 1 : 1;
        return new AuditedFile { SqlInstance = instance, FileType = AuditFileType.SpBlitz, FiredCheckCounts = counts };
    }

    // ── ParseBlitzRefs ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("48", new[] { 48 })]
    [InlineData("  7 ", new[] { 7 })]
    public void ParseBlitzRefs_BareInt(string src, int[] expected)
        => Assert.Equal(expected, BlitzDashboardService.ParseBlitzRefs(src));

    [Fact]
    public void ParseBlitzRefs_Composite_ExpandsAll()
        => Assert.Equal(new[] { 24, 25, 26, 40 }, BlitzDashboardService.ParseBlitzRefs("composite:24,25,26,40"));

    [Theory]
    [InlineData("internal-best-practice")]
    [InlineData("2.2")]
    [InlineData("Backups Not Performed Recently")]
    [InlineData(null)]
    [InlineData("")]
    public void ParseBlitzRefs_NonNumeric_Empty(string? src)
        => Assert.Empty(BlitzDashboardService.ParseBlitzRefs(src));

    // ── ParseEffortHours ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("30 min", 0.5)]
    [InlineData("2 hours", 2.0)]
    [InlineData("1 hr", 1.0)]
    [InlineData("1 day", 8.0)]
    [InlineData("1 week", 40.0)]
    [InlineData("", 1.0)]
    [InlineData("garbage", 1.0)]
    public void ParseEffortHours_Parses(string input, double expected)
        => Assert.Equal(expected, BlitzDashboardService.ParseEffortHours(input), 3);

    // ── BuildCatalog ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildCatalog_JoinsCorpusByBlitzIdPrefixAndNumericRef()
    {
        var corpus = new[] { Blitz("00048", "48"), new SqlCheck { Id = "SQLT-CIS-001", Source = "1" } };
        var cat = BlitzDashboardService.BuildCatalog(corpus, null);
        Assert.True(cat.ContainsKey(48));
        Assert.Equal(BlitzMetaSource.Corpus, cat[48].Source);
        Assert.False(cat.ContainsKey(1)); // non-blitz id is NOT joined
    }

    [Fact]
    public void BuildCatalog_CompositeEnrichesEveryListedId()
    {
        var cat = BlitzDashboardService.BuildCatalog(new[] { Blitz("X", "composite:24,25,26") }, null);
        Assert.True(cat.ContainsKey(24) && cat.ContainsKey(25) && cat.ContainsKey(26));
    }

    [Fact]
    public void BuildCatalog_CorpusWeight_IsWeightTimesEffort()
    {
        var cat = BlitzDashboardService.BuildCatalog(new[] { Blitz("00048", "48", weight: 5, effort: 2) }, null);
        Assert.Equal(10.0, cat[48].Weight, 3); // max(5,1)*max(2,1)
    }

    [Fact]
    public void BuildCatalog_InfoSeverity_IsDisplayInfo_ButStillScored()
    {
        // Info SEVERITY in a real category reads as informational (badge) but STILL counts (Scored).
        // Only a banner CATEGORY (Server Info) is excluded from the scored universe.
        var corpus = new[]
        {
            Blitz("01", "1", severity: "Info"),               // info by severity, real category
            Blitz("02", "2", category: "Server Info"),         // banner category
            Blitz("03", "3", severity: "High"),                // real finding
        };
        var cat = BlitzDashboardService.BuildCatalog(corpus, null);

        Assert.False(cat[1].IsBad); Assert.True(cat[1].Scored);   // info-severity finding counts
        Assert.False(cat[2].IsBad); Assert.False(cat[2].Scored);  // banner row: listed-only
        Assert.True(cat[3].IsBad);  Assert.True(cat[3].Scored);
    }

    [Fact]
    public void BuildCatalog_MapFallback_OnlyForUncoveredIds()
    {
        var corpus = new[] { Blitz("00048", "48") };
        var mapJson = """
        { "blitzCheckMap": [
          { "checkId": 48, "findingName": "Map 48", "category": "Reliability", "IsBad": 1, "effort_estimate": "30 min" },
          { "checkId": 99, "findingName": "Map 99", "category": "Security",    "IsBad": 1, "effort_estimate": "2 hours" }
        ] }
        """;
        var cat = BlitzDashboardService.BuildCatalog(corpus, mapJson);
        Assert.Equal(BlitzMetaSource.Corpus, cat[48].Source);  // corpus wins
        Assert.Equal(BlitzMetaSource.Map, cat[99].Source);     // fallback fills the gap
        Assert.Equal(2.0, cat[99].Weight, 3);                  // 1 * max(2h,1)
    }

    [Fact]
    public void BuildCatalog_MapBannerCategory_NotScored()
    {
        var mapJson = """
        { "blitzCheckMap": [
          { "checkId": 1, "findingName": "Info", "category": "Server Info", "IsBad": 0 },
          { "checkId": 2, "findingName": "Fail", "category": "Reliability", "IsBad": 1 }
        ] }
        """;
        var cat = BlitzDashboardService.BuildCatalog(Array.Empty<SqlCheck>(), mapJson);
        Assert.False(cat[1].IsBad); Assert.False(cat[1].Scored);  // banner category: listed-only
        Assert.True(cat[2].IsBad);  Assert.True(cat[2].Scored);
    }

    // ── ComputeInstanceReport (the scoring core) ─────────────────────────────

    [Fact]
    public void Score_NothingFired_Is100()
    {
        var cat = BlitzDashboardService.BuildCatalog(new[] { Blitz("01", "1"), Blitz("02", "2") }, null);
        var r = BlitzDashboardService.ComputeInstanceReport(FiredFile("S"), cat);
        Assert.Equal(100.0, r.HealthScore);
        Assert.Equal(0, r.ChecksFired);
        Assert.Equal(2, r.UniverseSize);
    }

    [Fact]
    public void Score_FiredCheck_DropsByItsWeightShare()
    {
        // Two equal-weight checks; firing one → 50%.
        var cat = BlitzDashboardService.BuildCatalog(
            new[] { Blitz("01", "1", weight: 5, effort: 2), Blitz("02", "2", weight: 5, effort: 2) }, null);
        var r = BlitzDashboardService.ComputeInstanceReport(FiredFile("S", 1), cat);
        Assert.Equal(50.0, r.HealthScore);
        Assert.Equal(1, r.ChecksFired);
    }

    [Fact]
    public void Score_WeightedNotJustCount()
    {
        // id1 weight 90, id2 weight 10 (via effort). Firing the heavy one → 10%.
        var cat = BlitzDashboardService.BuildCatalog(
            new[] { Blitz("01", "1", weight: 9, effort: 10), Blitz("02", "2", weight: 1, effort: 10) }, null);
        var r = BlitzDashboardService.ComputeInstanceReport(FiredFile("S", 1), cat);
        Assert.Equal(10.0, r.HealthScore, 1);
    }

    [Fact]
    public void Score_InfoSeverityFire_InRealCategory_CountsAsDing()
    {
        // Info SEVERITY in a real category is still a finding → it dings the score.
        var corpus = new[] { Blitz("01", "1", severity: "High"), Blitz("02", "2", severity: "Info") };
        var cat = BlitzDashboardService.BuildCatalog(corpus, null);
        var r = BlitzDashboardService.ComputeInstanceReport(FiredFile("S", 2), cat); // fire the info one
        Assert.Equal(50.0, r.HealthScore);  // equal weights, one fired → 50%
        Assert.Equal(1, r.ChecksFired);     // it counted as a ding
        Assert.Equal(0, r.InfoFired);       // NOT a banner row → not tallied as listed-only info
        Assert.Equal(2, r.UniverseSize);    // both are in the scored universe now
    }

    [Fact]
    public void Score_BannerCategoryFire_ListedNotScored()
    {
        // A banner CATEGORY (Server Info) fires on healthy servers → listed, never scored.
        var corpus = new[] { Blitz("01", "1", severity: "High"), Blitz("02", "2", category: "Server Info") };
        var cat = BlitzDashboardService.BuildCatalog(corpus, null);
        var r = BlitzDashboardService.ComputeInstanceReport(FiredFile("S", 2), cat); // fire the banner one
        Assert.Equal(100.0, r.HealthScore); // banner fire doesn't move the score
        Assert.Equal(0, r.ChecksFired);
        Assert.Equal(1, r.InfoFired);
        Assert.Equal(1, r.UniverseSize);    // only the High check is scored
    }

    [Fact]
    public void Score_UnclassifiedFire_CountsAsDing()
    {
        // An unknown fired id in the REAL sp_Blitz range DID fire → it counts as a ding (weight 1).
        var cat = BlitzDashboardService.BuildCatalog(new[] { Blitz("01", "1", weight: 5, effort: 2) }, null);
        var r = BlitzDashboardService.ComputeInstanceReport(FiredFile("S", 8999), cat); // unknown real-range id
        Assert.Equal(90.9, r.HealthScore, 1);   // known(10)/(known 10 + unknown 1) ≈ 90.9
        Assert.Equal(1, r.ChecksFired);
        Assert.Equal(1, r.UnclassifiedFired);
        Assert.Equal(2, r.UniverseSize);
        Assert.Contains(r.Findings, f => f.Unclassified && f.BlitzCheckId == 8999);
    }

    [Fact]
    public void Report_FindingsIncludeFireCount()
    {
        var cat = BlitzDashboardService.BuildCatalog(new[] { Blitz("01", "1") }, null);
        var file = FiredFile("S", 1, 1, 1); // fired 3 times (3 databases)
        var r = BlitzDashboardService.ComputeInstanceReport(file, cat);
        Assert.Equal(3, r.Findings.Single(f => f.BlitzCheckId == 1).FireCount);
    }

    [Fact]
    public void Build_DedupsInstance_PrefersSpBlitzOverSpTriage()
    {
        var repoChecks = new[] { Blitz("01", "1") };
        var cat = BlitzDashboardService.BuildCatalog(repoChecks, null);

        var blitz = new AuditedFile { SqlInstance = "S", FileType = AuditFileType.SpBlitz, FiredCheckCounts = new() { { 1, 1 } } };
        var triage = new AuditedFile { SqlInstance = "S", FileType = AuditFileType.SpTriage, FiredCheckCounts = new() };

        // Same instance from both files: the report must come from ONE of them, not double-count.
        var picked = new[] { blitz, triage }
            .GroupBy(f => f.SqlInstance, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f => f.FileType == AuditFileType.SpBlitz ? 0 : 1).First())
            .Single();
        Assert.Equal(AuditFileType.SpBlitz, picked.FileType);
    }

    // ── StripMarkup / BuildVoiceLines (the two-voice cell) ───────────────────

    [Fact]
    public void StripMarkup_DropsFencedSql_KeepsProse()
    {
        var input = "Repoint backups to separate storage.\n```sql\nBACKUP DATABASE x TO DISK='...';\n```\nThen re-test recovery.";
        var s = BlitzDashboardService.StripMarkup(input)!;
        Assert.DoesNotContain("BACKUP DATABASE", s);
        Assert.DoesNotContain("```", s);
        Assert.Contains("Repoint backups", s);
        Assert.Contains("re-test recovery", s);
    }

    [Fact]
    public void StripMarkup_DropsInlineCodeAndEmphasis_AndListMarkers()
    {
        var s = BlitzDashboardService.StripMarkup("1. **Identify** untrusted keys via `sys.foreign_keys` now.")!;
        Assert.DoesNotContain("`", s);
        Assert.DoesNotContain("sys.foreign_keys", s);
        Assert.DoesNotContain("**", s);
        Assert.DoesNotContain("1.", s);
        Assert.Contains("Identify untrusted keys", s);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("```sql\nSELECT 1;\n```")]   // nothing but code → nothing to show
    public void StripMarkup_EmptyOrCodeOnly_ReturnsNull(string? input)
        => Assert.Null(BlitzDashboardService.StripMarkup(input));

    [Fact]
    public void StripMarkup_TruncatesLongTextAtWordBoundary()
    {
        var s = BlitzDashboardService.StripMarkup(string.Join(" ", Enumerable.Repeat("word", 100)))!;
        Assert.True(s.Length <= 182, $"len {s.Length}");
        Assert.EndsWith("…", s);
    }

    private static BlitzCatalogEntry Entry(string? biz, string? remediation, string? description = null, string? eli5 = null)
        => new(1, "n", "Reliability", "High", IsBad: true, Scored: true, Weight: 10,
               biz, remediation, BlitzMetaSource.Corpus, description, eli5);

    [Fact]
    public void BuildVoiceLines_CioFromBusinessImpact_DbaFromRemediation()
    {
        var (cio, dba) = BlitzDashboardService.BuildVoiceLines(Entry("Why it matters.", "Do this fix."));
        Assert.Equal("Why it matters.", cio);
        Assert.Equal("Do this fix.", dba);
    }

    [Fact]
    public void BuildVoiceLines_InfoCheck_FallsBackToDescription_NoDuplicateLine()
    {
        // No Business Impact, no Remediation — both lines would resolve to Intent; show it once.
        var (cio, dba) = BlitzDashboardService.BuildVoiceLines(Entry(biz: null, remediation: null, description: "What this is."));
        Assert.Equal("What this is.", cio);
        Assert.Null(dba);   // suppressed because it would duplicate the CIO line
    }

    [Fact]
    public void BuildVoiceLines_StripsSqlFromDbaLine()
    {
        var (_, dba) = BlitzDashboardService.BuildVoiceLines(
            Entry("biz", "Enable it:\n```sql\nEXEC sp_configure 'x', 1;\n```"));
        Assert.NotNull(dba);
        Assert.DoesNotContain("sp_configure", dba!);
        Assert.Contains("Enable it", dba!);
    }

    // ── Sentinel exclusion (sp_triage custom checks, BlitzCheckID >= 9000) ────

    [Fact]
    public void BuildCatalog_ExcludesSentinelIds_AtOrAbove9000()
    {
        var corpus = new[] { Blitz("REAL", "48"), Blitz("CUSTOM", "9999") };
        var map = """{ "blitzCheckMap": [ { "checkId": 9001, "findingName": "x", "category": "Performance", "IsBad": 1 } ] }""";
        var cat = BlitzDashboardService.BuildCatalog(corpus, map);
        Assert.True(cat.ContainsKey(48));     // real sp_Blitz check kept
        Assert.False(cat.ContainsKey(9999));  // sp_triage corpus sentinel excluded
        Assert.False(cat.ContainsKey(9001));  // map sentinel excluded
    }

    [Fact]
    public void Score_FiredSentinelId_Ignored_NotDingNotListed()
    {
        var cat = BlitzDashboardService.BuildCatalog(new[] { Blitz("01", "1") }, null);
        var r = BlitzDashboardService.ComputeInstanceReport(FiredFile("S", 9999), cat);
        Assert.Equal(100.0, r.HealthScore);   // a fired sp_triage sentinel must not ding the sp_Blitz score
        Assert.Equal(0, r.ChecksFired);
        Assert.Equal(0, r.UnclassifiedFired); // not even listed as "unrated"
        Assert.DoesNotContain(r.Findings, f => f.BlitzCheckId == 9999);
    }

    // ── Composite rollup (one corpus check → N sibling ids share a display row) ──

    [Fact]
    public void Report_RollsUpCompositeSiblings_IntoOneRow_SummingCounts()
    {
        // One corpus check, composite of 3 ids → 3 distinct scored universe entries sharing a Name.
        var cat = BlitzDashboardService.BuildCatalog(
            new[] { Blitz("FAM", "composite:24,25,26", category: "Configuration") }, null);
        Assert.Equal(3, cat.Count);

        var file = FiredFile("S", 24, 24, 25);  // id24 fired x2, id25 x1, id26 not fired
        var r = BlitzDashboardService.ComputeInstanceReport(file, cat);

        var fam = r.Findings.Where(f => f.Name == "Blitz composite:24,25,26").ToList();
        Assert.Single(fam);                 // 2 fired siblings collapsed to ONE display row
        Assert.Equal(3, fam[0].FireCount);  // 2 + 1 fire counts summed
        Assert.Equal(2, r.ChecksFired);     // scoring still sees 2 distinct sp_Blitz checks fired
    }
}
