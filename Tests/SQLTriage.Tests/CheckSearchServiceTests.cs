/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class CheckSearchServiceTests : IDisposable
    {
        private readonly CheckRepositoryService _repo;
        private readonly CheckSearchService _search;

        public CheckSearchServiceTests()
        {
            // Populate 50 fixture checks with varied categories and searchable text
            var checks = new List<SqlCheck>();
            for (var i = 1; i <= 50; i++)
            {
                checks.Add(new SqlCheck
                {
                    Id = $"CHK-{i:D3}",
                    Name = i switch
                    {
                        <= 10 => $"Memory {(i <= 3 ? "Page Life Expectancy" : i <= 6 ? "Memory Grants Pending" : "Buffer Cache Hit Ratio")} Check {i}",
                        <= 20 => $"CPU {(i <= 13 ? "Scheduler Yield" : i <= 17 ? "Signal Wait" : "Operator Time")} Check {i}",
                        <= 30 => $"Security {(i <= 23 ? "Login Audit" : i <= 27 ? "Permission Check" : "Credential Scan")} Check {i}",
                        <= 40 => $"Index {(i <= 33 ? "Missing Index" : i <= 37 ? "Fragmentation" : "Duplicate Index")} Check {i}",
                        _     => $"Disk {(i <= 43 ? "IO Latency" : i <= 47 ? "Queue Depth" : "Free Space")} Check {i}"
                    },
                    Description = i switch
                    {
                        1 => "Checks page life expectancy against minimum threshold of 300 seconds",
                        2 => "Monitors memory grants pending in RESOURCE_SEMAPHORE",
                        3 => "Verifies buffer cache hit ratio stays above 90%",
                        _ => $"Generic description for check {i} covering {GetCategoryName(i)} metrics"
                    },
                    Category = GetCategoryName(i),
                    Severity = i <= 10 ? "Critical" : "Warning",
                    SqlQuery = $"SELECT {i} AS value /* check: {GetCategoryName(i)} */_SQLBODY_{i}",
                    Enabled = true
                });
            }

            _repo = new CheckRepositoryService(
                NullLogger<CheckRepositoryService>.Instance,
                configuration: null,
                bundle: null);

            // Seed checks via reflection into the private _checks field
            var checksField = typeof(CheckRepositoryService).GetField("_checks",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (checksField != null)
            {
                checksField.SetValue(_repo, checks);
            }

            // Add framework mappings for security checks (to test framework search)
            var fwProp = typeof(CheckRepositoryService).GetProperty("FrameworkMappings",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (fwProp != null)
            {
                var fw = new Dictionary<string, IReadOnlyList<SQLTriage.Data.Parser.FrameworkMapping>>
                {
                    ["CHK-023"] = new List<SQLTriage.Data.Parser.FrameworkMapping>
                    {
                        new("CIS 2.5", "2.5", "Audit Login Failures", "direct", new Dictionary<string, string>())
                    },
                    ["CHK-024"] = new List<SQLTriage.Data.Parser.FrameworkMapping>
                    {
                        new("NIST 800-53", "AC-7", "Unsuccessful Login Attempts", "mapped", new Dictionary<string, string>())
                    }
                };
                fwProp.SetValue(_repo, fw);
            }

            _search = new CheckSearchService(_repo, NullLogger<CheckSearchService>.Instance, bundle: null);
        }

        [Fact]
        public void Search_Memory_ReturnsMemoryRelatedChecks()
        {
            var results = _search.Search("memory");
            Assert.NotEmpty(results);
            Assert.All(results, r => Assert.True(
                r.CheckId.StartsWith("CHK-") && int.TryParse(r.CheckId[4..], out var n) && n <= 10,
                $"Expected memory check (CHK-001 to CHK-010), got {r.CheckId}"));
            Assert.True(results.Count >= 3, $"Expected at least 3 memory hits, got {results.Count}");
        }

        [Fact]
        public void Search_EmptyQuery_ReturnsNoHits()
        {
            var results = _search.Search("");
            Assert.Empty(results);

            results = _search.Search("  ");
            Assert.Empty(results);
        }

        [Fact]
        public void Search_FrameworkMapping_CisKeywordMatches()
        {
            var results = _search.Search("CIS");
            Assert.Contains(results, r => r.CheckId == "CHK-023");
        }

        [Fact]
        public void Search_FrameworkMapping_ControlIdMatches()
        {
            var results = _search.Search("AC-7");
            Assert.Contains(results, r => r.CheckId == "CHK-024");
        }

        [Fact]
        public void Search_TopK_LimitsResults()
        {
            var results = _search.Search("Check", topK: 5);
            Assert.True(results.Count <= 5);
        }

        [Fact]
        public void Search_SqlBody_MatchesSqlOnlyTerm()
        {
            // SQLBODY marker only exists in sql_body fixture column
            var results = _search.Search("SQLBODY");
            Assert.NotEmpty(results);
            Assert.All(results, r => Assert.Equal("sql_body", r.MatchedColumn));
        }

        [Fact]
        public void Search_ReturnsSnippet_WithHighlightTags()
        {
            var results = _search.Search("life expectancy");
            Assert.NotEmpty(results);
            foreach (var r in results)
            {
                Assert.Contains("<b>", r.Snippet);
                Assert.Contains("</b>", r.Snippet);
            }
        }

        [Fact]
        public void Search_NoResults_ForNonexistentTerm()
        {
            var results = _search.Search("zzzQUUX_nonexistent_xyzzy");
            Assert.Empty(results);
        }

        [Fact]
        public void Search_ResultsAreIdempotent()
        {
            var results1 = _search.Search("memory");
            var results2 = _search.Search("memory");
            Assert.Equal(results1.Count, results2.Count);
            for (var i = 0; i < results1.Count; i++)
            {
                Assert.Equal(results1[i].CheckId, results2[i].CheckId);
            }
        }

        [Fact]
        public void SanitizeFts5Query_EscapesTerms()
        {
            var sanitized = CheckSearchService.SanitizeFts5Query("hello world");
            Assert.Contains("\"hello\"", sanitized);
            Assert.Contains("\"world\"", sanitized);
            Assert.Contains(" OR ", sanitized);
        }

        [Fact]
        public void SanitizeFts5Query_SingleTerm_NoOrOperator()
        {
            var sanitized = CheckSearchService.SanitizeFts5Query("hello");
            Assert.Equal("\"hello\"", sanitized);
        }

        [Fact]
        public void SanitizeFts5Query_EmptyInput_ReturnsEmpty()
        {
            Assert.Empty(CheckSearchService.SanitizeFts5Query(""));
            Assert.Empty(CheckSearchService.SanitizeFts5Query("   "));
        }

        private static string GetCategoryName(int i) => i switch
        {
            <= 10 => "Memory",
            <= 20 => "CPU",
            <= 30 => "Security",
            <= 40 => "Indexing",
            _     => "Disk"
        };

        public void Dispose()
        {
            _search.Dispose();
        }
    }
}
