/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests
{
    public class ErrorCatalogTests
    {
        private static readonly string GoldenFilePath =
            Path.Combine("GoldenFiles", "error-catalog.golden.json");

        private static string? FindRealCatalogJson()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Config", "error-catalog.json");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = dir.Parent;
            }
            return null;
        }

        private static ErrorCatalog CreateCatalog()
        {
            var json = FindRealCatalogJson();
            var bundle = new FakeBundleAccessor();
            if (json != null) bundle.PutFile("Config/error-catalog.json", json);
            return new ErrorCatalog(NullLogger<ErrorCatalog>.Instance, bundle);
        }

        [Fact]
        public async Task Catalog_Loads_From_Committed_File()
        {
            var catalog = CreateCatalog();
            await catalog.ReloadAsync();

            Assert.True(catalog.Count > 0, "Catalog should contain entries");
        }

        [Theory]
        [InlineData("CONN_TIMEOUT")]
        [InlineData("CONN_AUTH_FAILURE")]
        [InlineData("QUERY_DEADLOCK")]
        [InlineData("PERM_DENIED")]
        [InlineData("CRED_EXPIRED")]
        [InlineData("RESOURCE_CPU_HIGH")]
        [InlineData("CONFIG_INVALID")]
        public async Task KnownEntries_Have_All_Fields_Populated(string errorCode)
        {
            var catalog = CreateCatalog();
            await catalog.ReloadAsync();

            var entry = catalog.Get(errorCode);
            Assert.NotNull(entry);
            Assert.False(string.IsNullOrWhiteSpace(entry.ErrorCode));
            Assert.False(string.IsNullOrWhiteSpace(entry.Category));
            Assert.False(string.IsNullOrWhiteSpace(entry.UserMessage));
            Assert.False(string.IsNullOrWhiteSpace(entry.GovernanceImpact));
            Assert.False(string.IsNullOrWhiteSpace(entry.Remediation));
            Assert.True(entry.AudienceMessages.ContainsKey(ErrorAudiences.Dba), "DBA message required");
            Assert.True(entry.AudienceMessages.ContainsKey(ErrorAudiences.ItManager), "IT message required");
            Assert.True(entry.AudienceMessages.ContainsKey(ErrorAudiences.Executive), "Exec message required");

            Assert.All(entry.AudienceMessages.Values, msg =>
                Assert.False(string.IsNullOrWhiteSpace(msg), "Audience message must not be empty"));
        }

        [Fact]
        public async Task GetMessage_Formats_Params()
        {
            var catalog = CreateCatalog();
            await catalog.ReloadAsync();

            var msg = catalog.GetMessage("CONN_TIMEOUT", ErrorAudiences.Dba, 30);
            Assert.Contains("30", msg);
        }

        [Fact]
        public async Task GetMessage_UnknownCode_Returns_Fallback()
        {
            var catalog = CreateCatalog();
            await catalog.ReloadAsync();

            var msg = catalog.GetMessage("DOES_NOT_EXIST");
            Assert.Contains("DOES_NOT_EXIST", msg);
        }

        [Fact]
        public async Task Search_Finds_By_Keyword()
        {
            var catalog = CreateCatalog();
            await catalog.ReloadAsync();

            var results = catalog.Search("deadlock");
            Assert.Single(results);
            Assert.Equal("QUERY_DEADLOCK", results[0].ErrorCode);
        }

        [Fact]
        public async Task GetByCategory_Filters_Correctly()
        {
            var catalog = CreateCatalog();
            await catalog.ReloadAsync();

            var conn = catalog.GetByCategory("Connection");
            Assert.True(conn.Count >= 2, "Should have at least 2 Connection entries");
            Assert.All(conn, e => Assert.Equal("Connection", e.Category));
        }

        [Fact]
        public async Task All_Entries_Have_Required_Fields()
        {
            var catalog = CreateCatalog();
            await catalog.ReloadAsync();

            foreach (var cat in ErrorCategories.All)
            {
                var list = catalog.GetByCategory(cat);
                if (list.Count == 0) continue;

                foreach (var e in list)
                {
                    Assert.False(string.IsNullOrWhiteSpace(e.ErrorCode), $"{cat} entry missing ErrorCode");
                    Assert.False(string.IsNullOrWhiteSpace(e.UserMessage), $"{e.ErrorCode} missing UserMessage");
                    Assert.False(string.IsNullOrWhiteSpace(e.GovernanceImpact), $"{e.ErrorCode} missing GovernanceImpact");
                    Assert.False(string.IsNullOrWhiteSpace(e.Remediation), $"{e.ErrorCode} missing Remediation");
                    Assert.True(e.AudienceMessages.Count >= 3, $"{e.ErrorCode} missing audience messages");
                }
            }
        }

        [Fact]
        public async Task GoldenFile_Matches_Committed_Catalog()
        {
            var catalog = CreateCatalog();
            await catalog.ReloadAsync();

            var entries = ErrorCategories.All
                .SelectMany(c => catalog.GetByCategory(c))
                .DistinctBy(e => e.ErrorCode)
                .OrderBy(e => e.ErrorCode)
                .ToList();

            Assert.True(entries.Count > 0, "No entries to snapshot");

            var snapshot = JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            Directory.CreateDirectory(Path.GetDirectoryName(GoldenFilePath)!);
            if (!File.Exists(GoldenFilePath))
            {
                await File.WriteAllTextAsync(GoldenFilePath, snapshot);
                Assert.Fail("Golden file did not exist; created it. Re-run test to validate.");
            }

            var golden = await File.ReadAllTextAsync(GoldenFilePath);
            Assert.Equal(golden, snapshot);
        }
    }
}
