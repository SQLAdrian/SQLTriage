/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class ConfigBaselineServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public ConfigBaselineServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "baseline-tests-" + Guid.NewGuid().ToString("N"));

            // ConfigBaselineService looks for "Config/*.json" relative to baseDir.
            // Create the Config sub-directory so ComputeHashes() finds our test files.
            Directory.CreateDirectory(Path.Combine(_tempDir, "Config"));
        }

        public void Dispose()
        {
            try
            {
                // Clear Hidden attributes set by ConfigBaselineService on the snapshot file.
                foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(_tempDir, recursive: true);
            }
            catch { /* test cleanup; ignore */ }
        }

        private ConfigBaselineService NewService()
            => new(NullLogger<ConfigBaselineService>.Instance, audit: null, baseDir: _tempDir);

        private void WriteConfig(string name, string content)
            => File.WriteAllText(Path.Combine(_tempDir, "Config", name), content);

        // ── T6: renamed file is reported as drift (1 delete + 1 add) ────

        [Fact]
        public void ComputeDrift_RenamedFile_ReportedAsDeletedAndAdded()
        {
            // Arrange: create 3 config files and snapshot them as the baseline.
            WriteConfig("alpha.json", "{\"a\":1}");
            WriteConfig("beta.json", "{\"b\":2}");
            WriteConfig("gamma.json", "{\"g\":3}");

            var svc = NewService();
            svc.RunStartupCheck(); // creates baseline (first run, no existing snapshot)
            Assert.False(svc.DriftDetected, "Should be no drift immediately after baseline is set.");

            // Act: rename beta.json → delta.json (simulate a file rename)
            File.Move(
                Path.Combine(_tempDir, "Config", "beta.json"),
                Path.Combine(_tempDir, "Config", "delta.json"));

            // Re-run startup check — reloads the baseline and diffs against current state.
            var svc2 = NewService();
            svc2.RunStartupCheck();

            // Assert: drift detected
            Assert.True(svc2.DriftDetected,
                "DriftDetected should be true after a file rename.");

            // The rename produces one deletion (beta.json) and one addition (delta.json).
            int deletions = 0, additions = 0;
            foreach (var entry in svc2.DriftedFiles)
            {
                if (entry.Contains("(deleted)", StringComparison.OrdinalIgnoreCase)) deletions++;
                if (entry.Contains("(added)", StringComparison.OrdinalIgnoreCase)) additions++;
            }

            Assert.Equal(1, deletions);
            Assert.Equal(1, additions);
        }
    }
}
