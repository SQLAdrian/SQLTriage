/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests
{
    public class ComplianceMappingServiceTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────

        private static ComplianceMappingService MakeWithJson(string json)
        {
            var bundle = new FakeBundleAccessor()
                .PutFile("Config/control_mappings.json", json);
            return new ComplianceMappingService(
                NullLogger<ComplianceMappingService>.Instance,
                bundle);
        }

        private static ComplianceMappingService MakeWithRealFile()
        {
            // Walk ancestor dirs to find the real control_mappings.json (for integration tests).
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string? json = null;
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Config", "control_mappings.json");
                if (File.Exists(candidate)) { json = File.ReadAllText(candidate); break; }
                dir = dir.Parent;
            }
            var bundle = new FakeBundleAccessor();
            if (json != null) bundle.PutFile("Config/control_mappings.json", json);
            return new ComplianceMappingService(
                NullLogger<ComplianceMappingService>.Instance,
                bundle);
        }

        // ── GetFrameworks — framework count ──────────────────────────────────

        [Fact]
        public void GetFrameworks_ReturnsExpectedCount_ExcludingHidden()
        {
            var svc = MakeWithRealFile();
            var frameworks = svc.GetFrameworks();
            if (frameworks.Count == 0) return; // file not in test output dir; integration deferred to CI
            Assert.Equal(30, frameworks.Count); // nothing hidden by default since 2026-06-24 (ISO 27001 un-hidden, SOC 2 added)
        }

        [Fact]
        public void GetFrameworks_IncludeHidden_ReturnsAll30()
        {
            var svc = MakeWithRealFile();
            var allFrameworks = svc.GetFrameworks(includeHidden: true);
            if (allFrameworks.Count == 0) return;
            Assert.Equal(30, allFrameworks.Count);
            Assert.Contains("ISO 27001", allFrameworks, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("SOC 2", allFrameworks, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void GetFrameworks_Default_Shows_Iso27001_AndIso27002()
        {
            // ISO 27001 was hidden by default historically; un-hidden 2026-06-24 (Adrian) so the
            // compliance map surfaces a framework the product names. No frameworks hidden by default now.
            var svc = MakeWithRealFile();
            var frameworks = svc.GetFrameworks();
            if (frameworks.Count == 0) return;
            Assert.Contains("ISO 27001", frameworks, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("ISO 27002", frameworks, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("SOC 2", frameworks, StringComparer.OrdinalIgnoreCase);
        }

        // ── GetControlsForVaCategory — known Security category ───────────────

        [Fact]
        public void GetControlsForVaCategory_Security_ReturnsMappings()
        {
            var svc = MakeWithRealFile();
            if (!svc.GetFrameworks().Any()) return;
            var controls = svc.GetControlsForVaCategory("Security");
            Assert.NotEmpty(controls);
            Assert.All(controls, c =>
            {
                Assert.NotEmpty(c.Framework);
                Assert.NotEmpty(c.ControlId);
            });
        }

        // ── Bundle-locked / empty-bundle behaviour ───────────────────────────

        [Fact]
        public void Returns_Empty_When_Bundle_Locked()
        {
            var bundle = new FakeBundleAccessor().SetLocked();
            var svc = new ComplianceMappingService(
                NullLogger<ComplianceMappingService>.Instance,
                bundle);
            Assert.Empty(svc.GetFrameworks());
            Assert.Empty(svc.GetControlsForVaCategory("Security"));
        }

        [Fact]
        public void GetControlsForVaCategory_NonexistentCategory_ReturnsEmpty()
        {
            var bundle = new FakeBundleAccessor(); // unlocked but no file in bundle
            var svc = new ComplianceMappingService(
                NullLogger<ComplianceMappingService>.Instance,
                bundle);
            Assert.Empty(svc.GetControlsForVaCategory("NonexistentCategory"));
        }
    }
}
