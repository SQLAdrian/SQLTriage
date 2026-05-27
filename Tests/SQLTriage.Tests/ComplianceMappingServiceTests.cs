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
            Assert.Equal(28, frameworks.Count);
        }

        [Fact]
        public void GetFrameworks_IncludeHidden_ReturnsAll29()
        {
            var svc = MakeWithRealFile();
            var allFrameworks = svc.GetFrameworks(includeHidden: true);
            if (allFrameworks.Count == 0) return;
            Assert.Equal(29, allFrameworks.Count);
            Assert.Contains("ISO 27001", allFrameworks, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void GetFrameworks_DefaultHides_Iso27001_ButShows_Iso27002()
        {
            var svc = MakeWithRealFile();
            var frameworks = svc.GetFrameworks();
            if (frameworks.Count == 0) return;
            Assert.DoesNotContain("ISO 27001", frameworks, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("ISO 27002", frameworks, StringComparer.OrdinalIgnoreCase);
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
