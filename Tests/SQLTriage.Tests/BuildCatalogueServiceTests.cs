/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests
{
    public class BuildCatalogueServiceTests
    {
        // Fixed reference date — pinned so lifecycle assertions are deterministic regardless of wall clock.
        private static readonly DateTime Today = new DateTime(2026, 5, 15);
        private static string D(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static BuildCatalogueService Build(string json) =>
            BuildCatalogueService.FromJson(json, NullLogger<BuildCatalogueService>.Instance, Today);

        // ── Availability ────────────────────────────────────────────────

        [Fact]
        public void Available_WhenJsonValid_IsTrue()
        {
            var svc = Build(SampleFullCatalogue());
            Assert.True(svc.IsAvailable);
            Assert.Equal("2026-05-01", svc.LastUpdated);
        }

        [Fact]
        public void Available_WhenJsonMalformed_IsFalse()
        {
            var svc = Build("{ not json at all");
            Assert.False(svc.IsAvailable);
            Assert.Null(svc.LastUpdated);
        }

        [Fact]
        public void Available_WhenEmptyObject_IsTrueButLookupsReturnNull()
        {
            var svc = Build("{}");
            Assert.True(svc.IsAvailable);
            Assert.Null(svc.Lookup("16.0.4115.5"));
        }

        // ── Null / empty / malformed build strings ──────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Lookup_NullOrWhitespace_ReturnsNull(string? build)
        {
            var svc = Build(SampleFullCatalogue());
            Assert.Null(svc.Lookup(build!));
        }

        [Fact]
        public void Lookup_NonNumericMajor_ReturnsNull()
        {
            var svc = Build(SampleFullCatalogue());
            Assert.Null(svc.Lookup("notaversion"));
        }

        [Fact]
        public void Lookup_UnknownMajorVersion_ReturnsNull()
        {
            // 17 is not yet mapped (no SQL Server "vNext" major handled).
            var svc = Build(SampleFullCatalogue());
            Assert.Null(svc.Lookup("17.0.1000.0"));
        }

        // ── Major version resolution ────────────────────────────────────

        [Theory]
        [InlineData("16.0.4115.5", "2022")]
        [InlineData("15.0.4316.3", "2019")]
        [InlineData("14.0.3460.9", "2017")]
        [InlineData("13.0.6419.1", "2016")]
        [InlineData("12.0.6164.21", "2014")]
        [InlineData("11.0.7507.2", "2012")]
        [InlineData("10.50.6000.34", "2008R2")]
        [InlineData("10.0.6000.29", "2008")]
        public void Lookup_MajorVersionMaps_AsExpected(string build, string expectedMajor)
        {
            var svc = Build(SampleAllVersionsCatalogue());
            var status = svc.Lookup(build);
            Assert.NotNull(status);
            Assert.Equal(expectedMajor, status!.MajorVersion);
        }

        [Fact]
        public void Lookup_VersionNotInCatalogue_ReturnsNull()
        {
            // Catalogue only has 2022; ask for 2019.
            var svc = Build(SampleFullCatalogue());
            Assert.Null(svc.Lookup("15.0.4316.3"));
        }

        [Fact]
        public void Lookup_VersionWithNullBuilds_ReturnsNull()
        {
            var json = """
            {
              "lastUpdated": "2026-05-01",
              "versions": { "2022": { "majorBuild": 16, "builds": null } }
            }
            """;
            var svc = Build(json);
            Assert.Null(svc.Lookup("16.0.4115.5"));
        }

        [Fact]
        public void Lookup_VersionWithEmptyBuilds_ReturnsNull()
        {
            var json = """
            {
              "lastUpdated": "2026-05-01",
              "versions": { "2022": { "majorBuild": 16, "builds": [] } }
            }
            """;
            var svc = Build(json);
            Assert.Null(svc.Lookup("16.0.4115.5"));
        }

        // ── Build matching (exact + predecessor) ────────────────────────

        [Fact]
        public void Lookup_ExactBuildMatch_ReturnsInstalledBuild()
        {
            var svc = Build(SampleFullCatalogue());
            var status = svc.Lookup("16.0.4115.5");
            Assert.NotNull(status);
            Assert.Equal("16.0.4115.5", status!.InstalledBuild);
            Assert.Equal("CU13", status.InstalledLabel);
        }

        [Fact]
        public void Lookup_BuildBetweenEntries_MatchesPredecessor()
        {
            // 16.0.4100.1 sits between CU12 (4095.4) and CU13 (4115.5) — should match CU12.
            var svc = Build(SampleFullCatalogue());
            var status = svc.Lookup("16.0.4100.1");
            Assert.NotNull(status);
            Assert.Equal("16.0.4095.4", status!.InstalledBuild);
            Assert.Equal("CU12", status.InstalledLabel);
        }

        [Fact]
        public void Lookup_BuildBeforeFirstEntry_ReturnsNull()
        {
            // 16.0.1000.0 is older than every catalogue entry — no predecessor exists.
            var svc = Build(SampleFullCatalogue());
            Assert.Null(svc.Lookup("16.0.1000.0"));
        }

        [Fact]
        public void Lookup_BuildAboveLatest_MatchesLatest()
        {
            // 16.0.9999.9 is newer than every entry — match the newest (CU14).
            var svc = Build(SampleFullCatalogue());
            var status = svc.Lookup("16.0.9999.9");
            Assert.NotNull(status);
            Assert.Equal("16.0.4131.2", status!.InstalledBuild);
            Assert.Equal(0, status.PatchesBehind);
        }

        [Fact]
        public void Lookup_BuildWithFewerSegments_StillCompares()
        {
            // "16.0.4115" (3 segments) should match the 4-segment "16.0.4115.5" exactly:
            // CompareBuildStrings zero-pads, so 16.0.4115.0 < 16.0.4115.5 → predecessor CU12.
            var svc = Build(SampleFullCatalogue());
            var status = svc.Lookup("16.0.4115");
            Assert.NotNull(status);
            Assert.Equal("16.0.4095.4", status!.InstalledBuild);
        }

        // ── Patches-behind counting ────────────────────────────────────

        [Fact]
        public void Lookup_OnLatestBuild_PatchesBehindIsZero()
        {
            var svc = Build(SampleFullCatalogue());
            var status = svc.Lookup("16.0.4131.2");
            Assert.NotNull(status);
            Assert.Equal(0, status!.PatchesBehind);
            Assert.Empty(status.MissedPatchesSample);
        }

        [Fact]
        public void Lookup_OnFirstBuild_PatchesBehindCountsAllNewer()
        {
            var svc = Build(SampleFullCatalogue());
            var status = svc.Lookup("16.0.1050.4"); // RTM in fixture
            Assert.NotNull(status);
            // Fixture has 5 builds; on RTM, 4 are newer.
            Assert.Equal(4, status!.PatchesBehind);
        }

        [Fact]
        public void Lookup_OnMiddleBuild_PatchesBehindCountsNewerOnly()
        {
            var svc = Build(SampleFullCatalogue());
            var status = svc.Lookup("16.0.4095.4"); // CU12
            Assert.NotNull(status);
            // CU13 and CU14 are newer.
            Assert.Equal(2, status!.PatchesBehind);
        }

        [Fact]
        public void Lookup_DaysBehind_UsesLatestMinusInstalled()
        {
            // Latest = 2026-04-01, CU12 installed at 2025-10-01 → 182 days behind.
            var svc = Build(SampleFullCatalogue());
            var status = svc.Lookup("16.0.4095.4");
            Assert.NotNull(status);
            Assert.Equal(182, status!.DaysBehind);
        }

        [Fact]
        public void Lookup_DaysBehind_OnLatestBuild_IsZero()
        {
            var svc = Build(SampleFullCatalogue());
            var status = svc.Lookup("16.0.4131.2");
            Assert.NotNull(status);
            Assert.Equal(0, status!.DaysBehind);
        }

        // ── MissedPatchesSample ─────────────────────────────────────────

        [Fact]
        public void Lookup_MissedPatchesSample_CappedAtTen()
        {
            // 12 newer patches in fixture → sample is capped at 10.
            var svc = Build(SampleManyPatchesCatalogue());
            var status = svc.Lookup("16.0.1050.4"); // RTM
            Assert.NotNull(status);
            Assert.Equal(12, status!.PatchesBehind);
            Assert.Equal(10, status.MissedPatchesSample.Count);
        }

        [Fact]
        public void Lookup_MissedPatchesSample_ContainsNewerEntriesInReleaseOrder()
        {
            var svc = Build(SampleFullCatalogue());
            var status = svc.Lookup("16.0.4095.4"); // CU12 → CU13, CU14 newer
            Assert.NotNull(status);
            Assert.Equal(2, status!.MissedPatchesSample.Count);
            Assert.Equal("16.0.4115.5", status.MissedPatchesSample[0].Build);
            Assert.Equal("CU13", status.MissedPatchesSample[0].Label);
            Assert.Equal("KB5036434", status.MissedPatchesSample[0].Kb);
            Assert.Equal("16.0.4131.2", status.MissedPatchesSample[1].Build);
            Assert.Equal("CU14", status.MissedPatchesSample[1].Label);
        }

        // ── Lifecycle status (depends on DateTime.UtcNow) ───────────────

        [Fact]
        public void Lifecycle_MainstreamInFarFuture_IsMainstream()
        {
            var json = LifecycleJson(
                mainstreamEnds: Today.AddYears(5),
                extendedEnds: Today.AddYears(10));
            var svc = Build(json);
            var status = svc.Lookup("16.0.4115.5");
            Assert.NotNull(status);
            Assert.Equal(LifecycleStatusKind.Mainstream, status!.LifecycleStatus);
            Assert.Equal("Mainstream support", status.LifecycleLabel);
        }

        [Fact]
        public void Lifecycle_MainstreamEndsWithinAYear_IsEndingSoon()
        {
            var json = LifecycleJson(
                mainstreamEnds: Today.AddDays(100),
                extendedEnds: Today.AddYears(5));
            var svc = Build(json);
            var status = svc.Lookup("16.0.4115.5");
            Assert.NotNull(status);
            Assert.Equal(LifecycleStatusKind.MainstreamEndingSoon, status!.LifecycleStatus);
            Assert.Equal("Mainstream ending soon", status.LifecycleLabel);
        }

        [Fact]
        public void Lifecycle_MainstreamPassed_ExtendedActive_IsExtendedOnly()
        {
            var json = LifecycleJson(
                mainstreamEnds: Today.AddDays(-30),
                extendedEnds: Today.AddYears(3));
            var svc = Build(json);
            var status = svc.Lookup("16.0.4115.5");
            Assert.NotNull(status);
            Assert.Equal(LifecycleStatusKind.ExtendedSupportOnly, status!.LifecycleStatus);
            Assert.Equal("Extended support only", status.LifecycleLabel);
        }

        [Fact]
        public void Lifecycle_ExtendedPassed_IsOutOfSupport()
        {
            var json = LifecycleJson(
                mainstreamEnds: Today.AddYears(-5),
                extendedEnds: Today.AddDays(-1));
            var svc = Build(json);
            var status = svc.Lookup("16.0.4115.5");
            Assert.NotNull(status);
            Assert.Equal(LifecycleStatusKind.OutOfSupport, status!.LifecycleStatus);
            Assert.Equal("Out of support", status.LifecycleLabel);
        }

        [Fact]
        public void Lifecycle_BothDatesNull_DefaultsToMainstream()
        {
            var json = LifecycleJson(mainstreamEnds: null, extendedEnds: null);
            var svc = Build(json);
            var status = svc.Lookup("16.0.4115.5");
            Assert.NotNull(status);
            Assert.Equal(LifecycleStatusKind.Mainstream, status!.LifecycleStatus);
        }

        // ── BuildStatus.AgeYears ────────────────────────────────────────

        [Fact]
        public void AgeYears_OnInstallReleasedTwoYearsAgo_IsTwo()
        {
            var twoYearsAgo = Today.AddYears(-2);
            var json = $$"""
            {
              "lastUpdated": "2026-05-01",
              "versions": {
                "2022": {
                  "majorBuild": 16,
                  "latestBuild": "16.0.4115.5",
                  "latestReleaseDate": "{{D(Today)}}",
                  "builds": [
                    { "build": "16.0.4115.5", "label": "CU13", "releaseDate": "{{D(twoYearsAgo)}}" }
                  ]
                }
              }
            }
            """;
            var svc = Build(json);
            var status = svc.Lookup("16.0.4115.5");
            Assert.NotNull(status);
            Assert.Equal(2, status!.AgeYears);
        }

        [Fact]
        public void AgeYears_WhenInstallDateMissing_IsNull()
        {
            var json = """
            {
              "lastUpdated": "2026-05-01",
              "versions": {
                "2022": {
                  "majorBuild": 16,
                  "latestBuild": "16.0.4115.5",
                  "builds": [
                    { "build": "16.0.4115.5", "label": "CU13" }
                  ]
                }
              }
            }
            """;
            var svc = Build(json);
            var status = svc.Lookup("16.0.4115.5");
            Assert.NotNull(status);
            Assert.Null(status!.AgeYears);
        }

        // ── LifecycleLabel exhaustiveness ───────────────────────────────

        [Theory]
        [InlineData(LifecycleStatusKind.Mainstream, "Mainstream support")]
        [InlineData(LifecycleStatusKind.MainstreamEndingSoon, "Mainstream ending soon")]
        [InlineData(LifecycleStatusKind.ExtendedSupportOnly, "Extended support only")]
        [InlineData(LifecycleStatusKind.OutOfSupport, "Out of support")]
        public void LifecycleLabel_KnownValues_HaveHumanReadableText(LifecycleStatusKind kind, string expected)
        {
            var status = new BuildStatus { LifecycleStatus = kind };
            Assert.Equal(expected, status.LifecycleLabel);
        }

        // ── Bundle-locked / empty-bundle behaviour ───────────────────────────

        [Fact]
        public void Returns_Empty_When_Bundle_Locked()
        {
            var bundle = new FakeBundleAccessor().SetLocked();
            var svc = new BuildCatalogueService(NullLogger<BuildCatalogueService>.Instance, bundle);
            Assert.False(svc.IsAvailable);
            Assert.Null(svc.Lookup("16.0.4115.5"));
        }

        // ── Real shipped catalogue (smoke test via FakeBundleAccessor) ──────

        [Fact]
        public void Bundle_WithRealCatalogueJson_IsAvailable()
        {
            var json = LoadRealCatalogueJson();
            if (json is null) return; // file not in test output dir; deferred to CI

            var bundle = new FakeBundleAccessor()
                .PutFile("Config/sql-build-catalogue.json", json);
            var svc = new BuildCatalogueService(NullLogger<BuildCatalogueService>.Instance, bundle);
            Assert.True(svc.IsAvailable);
            Assert.False(string.IsNullOrEmpty(svc.LastUpdated));
        }

        [Fact]
        public void Bundle_Catalogue_KnowsAbout2022Major()
        {
            var json = LoadRealCatalogueJson();
            if (json is null) return;

            var bundle = new FakeBundleAccessor()
                .PutFile("Config/sql-build-catalogue.json", json);
            var svc = new BuildCatalogueService(NullLogger<BuildCatalogueService>.Instance, bundle);
            var status = svc.Lookup("16.0.9999.9");
            Assert.NotNull(status);
            Assert.Equal("2022", status!.MajorVersion);
            Assert.True(status.PatchesBehind >= 0);
        }

        private static string? LoadRealCatalogueJson()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Config", "sql-build-catalogue.json");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = dir.Parent;
            }
            return null;
        }

        // ── Fixtures ────────────────────────────────────────────────────

        private static string SampleFullCatalogue() => $$"""
        {
          "lastUpdated": "2026-05-01",
          "versions": {
            "2022": {
              "majorBuild": 16,
              "mainstreamSupportEnds": "{{D(Today.AddYears(2))}}",
              "extendedSupportEnds": "{{D(Today.AddYears(7))}}",
              "latestBuild": "16.0.4131.2",
              "latestReleaseDate": "2026-04-01",
              "builds": [
                { "build": "16.0.1050.4", "label": "RTM",  "kb": "KB5023056", "releaseDate": "2022-11-16", "type": "RTM" },
                { "build": "16.0.4035.4", "label": "CU10", "kb": "KB5031778", "releaseDate": "2024-08-13", "type": "CU"  },
                { "build": "16.0.4095.4", "label": "CU12", "kb": "KB5035123", "releaseDate": "2025-10-01", "type": "CU"  },
                { "build": "16.0.4115.5", "label": "CU13", "kb": "KB5036434", "releaseDate": "2026-01-15", "type": "CU"  },
                { "build": "16.0.4131.2", "label": "CU14", "kb": "KB5037321", "releaseDate": "2026-04-01", "type": "CU"  }
              ]
            }
          }
        }
        """;

        private static string SampleManyPatchesCatalogue()
        {
            // 13 builds total: RTM + 12 CUs. RTM caller should see PatchesBehind=12 with sample capped at 10.
            var builds = new System.Text.StringBuilder();
            builds.Append("""{ "build": "16.0.1050.4", "label": "RTM", "releaseDate": "2022-11-16" }""");
            for (int i = 1; i <= 12; i++)
            {
                var date = new DateTime(2023, 1, 1).AddMonths(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                builds.Append($$$""", { "build": "16.0.{{{4000 + i}}}.0", "label": "CU{{{i}}}", "releaseDate": "{{{date}}}" }""");
            }
            return $$"""
            {
              "lastUpdated": "2026-05-01",
              "versions": {
                "2022": {
                  "majorBuild": 16,
                  "latestBuild": "16.0.4012.0",
                  "latestReleaseDate": "2024-01-01",
                  "builds": [ {{builds}} ]
                }
              }
            }
            """;
        }

        private static string SampleAllVersionsCatalogue() => """
        {
          "lastUpdated": "2026-05-01",
          "versions": {
            "2022":   { "majorBuild": 16,    "builds": [ { "build": "16.0.4115.5",  "label": "CU13", "releaseDate": "2026-01-15" } ] },
            "2019":   { "majorBuild": 15,    "builds": [ { "build": "15.0.4316.3",  "label": "CU25", "releaseDate": "2024-02-15" } ] },
            "2017":   { "majorBuild": 14,    "builds": [ { "build": "14.0.3460.9",  "label": "CU31", "releaseDate": "2022-09-20" } ] },
            "2016":   { "majorBuild": 13,    "builds": [ { "build": "13.0.6419.1",  "label": "SP3 CU1", "releaseDate": "2023-09-09" } ] },
            "2014":   { "majorBuild": 12,    "builds": [ { "build": "12.0.6164.21", "label": "SP3 CU4", "releaseDate": "2021-01-12" } ] },
            "2012":   { "majorBuild": 11,    "builds": [ { "build": "11.0.7507.2",  "label": "SP4 GDR", "releaseDate": "2022-07-12" } ] },
            "2008R2": { "majorBuild": 1050,  "builds": [ { "build": "10.50.6000.34","label": "SP3 GDR", "releaseDate": "2018-04-10" } ] },
            "2008":   { "majorBuild": 10,    "builds": [ { "build": "10.0.6000.29", "label": "SP4 GDR", "releaseDate": "2018-07-10" } ] }
          }
        }
        """;

        private static string LifecycleJson(DateTime? mainstreamEnds, DateTime? extendedEnds)
        {
            string MainStr = mainstreamEnds.HasValue ? $"\"{D(mainstreamEnds.Value)}\"" : "null";
            string ExtStr = extendedEnds.HasValue ? $"\"{D(extendedEnds.Value)}\"" : "null";
            return $$"""
            {
              "lastUpdated": "2026-05-01",
              "versions": {
                "2022": {
                  "majorBuild": 16,
                  "mainstreamSupportEnds": {{MainStr}},
                  "extendedSupportEnds": {{ExtStr}},
                  "latestBuild": "16.0.4115.5",
                  "latestReleaseDate": "2026-01-15",
                  "builds": [
                    { "build": "16.0.4115.5", "label": "CU13", "releaseDate": "2026-01-15" }
                  ]
                }
              }
            }
            """;
        }
    }
}
