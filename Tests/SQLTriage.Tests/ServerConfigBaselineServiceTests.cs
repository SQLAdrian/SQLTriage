using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class ServerConfigBaselineServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public ServerConfigBaselineServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "svcbaseline-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private ServerConfigBaselineService NewService(int retentionMonths = 12) =>
            new(NullLogger<ServerConfigBaselineService>.Instance,
                connectionManager: null,
                audit: null,
                configuration: null,
                dbPath: Path.Combine(_tempDir, "server-config-baselines.db"));

        // ── DiffConfig ───────────────────────────────────────────────────────

        [Fact]
        public void DiffConfig_AddedKey_IsReportedAsAdded()
        {
            var baseline = new Dictionary<string, ConfigEntry>
            {
                ["xp_cmdshell"] = new("0", "0", "Extended stored procedure"),
            };
            var current = new Dictionary<string, ConfigEntry>
            {
                ["xp_cmdshell"] = new("0", "0", "Extended stored procedure"),
                ["clr enabled"] = new("1", "1", "CLR user code execution"),
            };

            var diff = ServerConfigBaselineService.DiffConfig(baseline, current);

            Assert.Single(diff.Added);
            Assert.Equal("clr enabled", diff.Added[0].Key);
            Assert.Empty(diff.Removed);
            Assert.Empty(diff.Modified);
            Assert.True(diff.HasChanges);
        }

        [Fact]
        public void DiffConfig_RemovedKey_IsReportedAsRemoved()
        {
            var baseline = new Dictionary<string, ConfigEntry>
            {
                ["xp_cmdshell"] = new("0", "0", "desc"),
                ["clr enabled"] = new("1", "1", "desc"),
            };
            var current = new Dictionary<string, ConfigEntry>
            {
                ["xp_cmdshell"] = new("0", "0", "desc"),
            };

            var diff = ServerConfigBaselineService.DiffConfig(baseline, current);

            Assert.Single(diff.Removed);
            Assert.Equal("clr enabled", diff.Removed[0].Key);
            Assert.Empty(diff.Added);
            Assert.Empty(diff.Modified);
        }

        [Fact]
        public void DiffConfig_ModifiedValue_IsReportedAsModified()
        {
            var baseline = new Dictionary<string, ConfigEntry>
            {
                ["max degree of parallelism"] = new("0", "0", "desc"),
            };
            var current = new Dictionary<string, ConfigEntry>
            {
                ["max degree of parallelism"] = new("4", "4", "desc"),
            };

            var diff = ServerConfigBaselineService.DiffConfig(baseline, current);

            Assert.Single(diff.Modified);
            Assert.Equal("max degree of parallelism", diff.Modified[0].Key);
            Assert.Equal("0", diff.Modified[0].Baseline.Value);
            Assert.Equal("4", diff.Modified[0].Current.Value);
        }

        [Fact]
        public void DiffConfig_Identical_ReturnsNoChanges()
        {
            var config = new Dictionary<string, ConfigEntry>
            {
                ["xp_cmdshell"] = new("0", "0", "desc"),
                ["clr enabled"] = new("0", "0", "desc"),
            };

            var diff = ServerConfigBaselineService.DiffConfig(config, config);

            Assert.False(diff.HasChanges);
        }

        // ── GetBaselinesAsync — round-trip via direct insert ─────────────────

        [Fact]
        public async Task GetBaselinesAsync_EmptyDb_ReturnsEmpty()
        {
            using var svc = NewService();
            var list = await svc.GetBaselinesAsync("nonexistent-server");
            Assert.Empty(list);
        }

        // ── LoadBaselineAsync — missing id returns null ───────────────────────

        [Fact]
        public async Task LoadBaselineAsync_NonExistentId_ReturnsNull()
        {
            using var svc = NewService();
            var result = await svc.LoadBaselineAsync(99999);
            Assert.Null(result);
        }
    }
}
