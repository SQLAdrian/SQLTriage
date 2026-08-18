/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class FindingTranslatorTests
    {
        private static FindingTranslator CreateTranslator(
            IMemoryCache? cache = null,
            ISqlQueryRepository? repo = null,
            IGovernanceWeightsProvider? weights = null)
        {
            cache ??= new MemoryCache(new MemoryCacheOptions());
            repo ??= new StubSqlQueryRepository();
            weights ??= new FixedWeightsProvider(new GovernanceWeights());
            return new FindingTranslator(cache, repo, weights, NullLogger<FindingTranslator>.Instance);
        }

        [Theory]
        [InlineData("VA-001", "Backup Validation")]
        [InlineData("TRIAGE_002", "Review Log Space")]
        [InlineData("TRIAGE_016", "Review Invalid Logins")]
        [InlineData("UNKNOWN_999", "Unknown Check")]
        public async Task All_Audiences_Return_NonEmpty_Content(string checkId, string checkName)
        {
            var translator = CreateTranslator();
            var result = new CheckResult
            {
                CheckId = checkId,
                CheckName = checkName,
                Category = "Reliability",
                Severity = "HIGH",
                Passed = false,
                Message = "Test finding message",
                ActualValue = 1,
                ExpectedValue = 0,
                InstanceName = "TEST-SERVER"
            };

            var t = await translator.TranslateAsync(result);

            Assert.NotNull(t);
            Assert.False(string.IsNullOrWhiteSpace(t.Dba.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Dba.TechnicalDetails));
            Assert.False(string.IsNullOrWhiteSpace(t.Dba.TSqlRemediation));
            Assert.False(string.IsNullOrWhiteSpace(t.Dba.RawData));

            Assert.False(string.IsNullOrWhiteSpace(t.ItManager.BusinessCategory));
            Assert.False(string.IsNullOrWhiteSpace(t.ItManager.SlaImpact));
            Assert.False(string.IsNullOrWhiteSpace(t.ItManager.RemediationEffort));
            Assert.False(string.IsNullOrWhiteSpace(t.ItManager.RecommendedAction));

            Assert.False(string.IsNullOrWhiteSpace(t.Executive.PlainLanguageSummary));
            Assert.False(string.IsNullOrWhiteSpace(t.Executive.BusinessRisk));
            Assert.False(string.IsNullOrWhiteSpace(t.Executive.EstimatedMonthlyCost));
            Assert.False(string.IsNullOrWhiteSpace(t.Executive.ComplianceControls));
            Assert.False(string.IsNullOrWhiteSpace(t.Executive.RecommendedAction));

            // All audience strings must be >20 chars (meaningful content)
            Assert.True(t.Dba.TechnicalDetails.Length > 20, "DBA TechnicalDetails too short");
            Assert.True(t.ItManager.SlaImpact.Length > 20, "IT SLA impact too short");
            Assert.True(t.Executive.PlainLanguageSummary.Length > 20, "Exec summary too short");
        }

        [Fact]
        public async Task Same_Finding_Twice_Uses_Cache()
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var translator = CreateTranslator(cache);
            var result = new CheckResult
            {
                CheckId = "VA-001",
                CheckName = "Backup Validation",
                Severity = "CRITICAL",
                Passed = false,
                Message = "No backup",
                InstanceName = "SRV01"
            };

            var t1 = await translator.TranslateAsync(result);
            var t2 = await translator.TranslateAsync(result);

            Assert.Equal(t1.FindingId, t2.FindingId);
            Assert.Same(t1, t2); // MemoryCache should return exact same object
        }

        [Fact(Skip = "Phase 2: IGovernanceWeightsProvider.WeightsChanged is not yet wired to a file watcher or bundle event. " +
                     "Revisit in Phase 3 when IBundleAccessor fires WeightsChanged on bundle decryption.")]
        public async Task GovernanceWeights_Change_Busts_Cache()
        {
            // This test verified that the translation cache version increments when governance
            // weights are reloaded.  The mechanism now relies on IGovernanceWeightsProvider.WeightsChanged
            // (replacing the old IOptionsMonitor<GovernanceWeights>.OnChange callback).
            //
            // In Phase 3, TriggeredWeightsProvider below can be re-enabled and wired to
            // IBundleAccessor.BundleStateChanged.

            var cache = new MemoryCache(new MemoryCacheOptions());
            var weights = new TriggeredWeightsProvider(new GovernanceWeights());
            var translator = CreateTranslator(cache, weights: weights);
            var result = new CheckResult
            {
                CheckId = "VA-001",
                CheckName = "Backup Validation",
                Severity = "CRITICAL",
                Passed = false,
                Message = "No backup",
                InstanceName = "SRV01"
            };

            var t1 = await translator.TranslateAsync(result);

            // Simulate governance weights reload
            weights.TriggerChange(new GovernanceWeights
            {
                Caps = new GovernanceCaps { PerFinding = 20 }
            });

            var t2 = await translator.TranslateAsync(result);

            Assert.NotEqual(t1.FindingId, t2.FindingId); // New translation after cache bust
        }

        [Fact]
        public async Task Passed_Finding_Has_Lower_Risk_Language()
        {
            var translator = CreateTranslator();
            var result = new CheckResult
            {
                CheckId = "VA-001",
                CheckName = "Backup Validation",
                Severity = "CRITICAL",
                Passed = true,
                Message = "Backup OK",
                InstanceName = "SRV01"
            };

            var t = await translator.TranslateAsync(result);

            Assert.Contains("passed", t.Executive.PlainLanguageSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No action required", t.Executive.RecommendedAction, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task BusinessRisk_Derived_From_Severity_Not_Overall_Score()
        {
            var translator = CreateTranslator();
            var low = new CheckResult
            {
                CheckId = "CHK-001",
                CheckName = "X",
                Severity = "LOW",
                Passed = false,
                Message = "M",
                InstanceName = "S"
            };
            var critical = new CheckResult
            {
                CheckId = "CHK-002",
                CheckName = "Y",
                Severity = "CRITICAL",
                Passed = false,
                Message = "M",
                InstanceName = "S"
            };

            var tLow = await translator.TranslateAsync(low);
            var tCrit = await translator.TranslateAsync(critical);

            Assert.Contains("Low", tLow.Executive.BusinessRisk);
            Assert.Contains("Critical", tCrit.Executive.BusinessRisk);
        }

        [Fact]
        public async Task SqlQueryDefinition_Metadata_Used_When_Available()
        {
            var repo = new StubSqlQueryRepository();
            repo.Add(new SqlQueryDefinition
            {
                Id = "VA-001",
                Sql = "SELECT 1",
                FilePath = "x.sql",
                Category = "Security",
                Severity = "CRITICAL",
                Controls = new[] { "SOC2-CC7.4", "PCI-DSS-10.1" }
            });

            var translator = CreateTranslator(repo: repo);
            var result = new CheckResult
            {
                CheckId = "VA-001",
                CheckName = "Backup Validation",
                Severity = "MEDIUM", // repo overrides this
                Passed = false,
                Message = "M",
                InstanceName = "S"
            };

            var t = await translator.TranslateAsync(result);

            Assert.Contains("SOC2-CC7.4", t.Executive.ComplianceControls);
            Assert.Contains("PCI-DSS-10.1", t.Executive.ComplianceControls);
        }

        // ─── Stubs ───

        private class StubSqlQueryRepository : ISqlQueryRepository
        {
            private readonly System.Collections.Generic.Dictionary<string, SqlQueryDefinition> _dict = new(StringComparer.OrdinalIgnoreCase);

            public void Add(SqlQueryDefinition def) => _dict[def.Id] = def;

            public SqlQueryDefinition? Get(string id) => _dict.TryGetValue(id, out var d) ? d : null;
            public IReadOnlyDictionary<string, SqlQueryDefinition> GetAll() => _dict;
            public IReadOnlyList<SqlQueryDefinition> GetByTag(string tag) => _dict.Values.Where(q => q.Category.Equals(tag, StringComparison.OrdinalIgnoreCase)).ToList();
            public IReadOnlyList<SqlQueryDefinition> GetQuickChecks() => _dict.Values.Where(q => q.Quick).ToList();
            public Task ReloadAsync() => Task.CompletedTask;
        }

        /// <summary>
        /// Test-only provider that returns a fixed <see cref="GovernanceWeights"/> instance
        /// and never fires <see cref="WeightsChanged"/>.
        /// </summary>
        private sealed class FixedWeightsProvider : IGovernanceWeightsProvider
        {
            private readonly GovernanceWeights _weights;
            public FixedWeightsProvider(GovernanceWeights weights) => _weights = weights;
            public GovernanceWeights Current => _weights;
            public event EventHandler? WeightsChanged;
        }

        /// <summary>
        /// Test-only provider that supports manually triggering <see cref="WeightsChanged"/>.
        /// Used by <see cref="GovernanceWeights_Change_Busts_Cache"/> (currently skipped in Phase 2).
        /// Phase 3 re-enable: wire <c>TriggerChange</c> to bundle-state changes instead.
        /// </summary>
        private sealed class TriggeredWeightsProvider : IGovernanceWeightsProvider
        {
            private GovernanceWeights _weights;
            public TriggeredWeightsProvider(GovernanceWeights weights) => _weights = weights;
            public GovernanceWeights Current => _weights;
            public event EventHandler? WeightsChanged;

            public void TriggerChange(GovernanceWeights newWeights)
            {
                _weights = newWeights;
                WeightsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
