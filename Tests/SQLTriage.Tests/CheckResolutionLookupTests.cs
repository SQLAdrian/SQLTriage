/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// S4 build step: the reverse-index lookup wiring failed checks to their resolution
    /// (one-click RemediationTemplate or review-only maintenance generator).
    /// </summary>
    public class CheckResolutionLookupTests
    {
        private static CheckResolutionLookup NewLookup() =>
            new(new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance));

        // ── A check with a registered one-click template resolves to it ─────

        [Fact]
        public void CheckWithTemplate_ResolvesToOneClickTemplate()
        {
            // SQLT-BLITZ-NO-OPERATORS is one of AGENTALERTPACK's ResolvesCheckIds.
            var res = NewLookup().Resolve("SQLT-BLITZ-NO-OPERATORS");
            Assert.NotNull(res);
            Assert.True(res!.IsOneClick);
            Assert.False(res.IsMaintenance);
            Assert.Equal("AGENTALERTPACK", res.Template!.Key);
        }

        [Fact]
        public void CheckWithTemplate_InstallMaintenanceSolution_Resolves()
        {
            // SQLT-BLITZ-DBCC-CHECKDB-NOT-PERFORMED-RECENTLY is one of
            // INSTALLMAINTENANCESOLUTION's ResolvesCheckIds — distinct from the
            // review-only SQLT-BPCHK-DBCC-CHECKDB-STATUS maintenance check below.
            var res = NewLookup().Resolve("SQLT-BLITZ-DBCC-CHECKDB-NOT-PERFORMED-RECENTLY");
            Assert.NotNull(res);
            Assert.True(res!.IsOneClick);
            Assert.Equal("INSTALLMAINTENANCESOLUTION", res.Template!.Key);
        }

        // ── A check with no template and no maintenance mapping resolves to null ─

        [Fact]
        public void CheckWithNoResolution_ResolvesToNull()
        {
            var res = NewLookup().Resolve("SQLT-CORE-00170"); // arbitrary unmapped check id
            Assert.Null(res);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NullOrBlankCheckId_ResolvesToNull(string? checkId)
        {
            Assert.Null(NewLookup().Resolve(checkId));
        }

        // ── The three MAINTENANCE checks map to their generator, review-only ─

        [Fact]
        public void IndexFragmentationCheck_ResolvesToIndexGenerator()
        {
            var res = NewLookup().Resolve("SQLT-CUSTOM-INDEX-FRAGMENTATION");
            Assert.NotNull(res);
            Assert.True(res!.IsMaintenance);
            Assert.False(res.IsOneClick);
            Assert.Equal(MaintenanceGenerator.IndexFragmentation, res.Generator);
        }

        [Fact]
        public void StatisticsHealthCheck_ResolvesToStatisticsGenerator()
        {
            var res = NewLookup().Resolve("SQLT-CUSTOM-STATISTICS-HEALTH");
            Assert.NotNull(res);
            Assert.True(res!.IsMaintenance);
            Assert.Equal(MaintenanceGenerator.Statistics, res.Generator);
        }

        [Fact]
        public void DbccCheckdbStatusCheck_ResolvesToCheckDbGenerator()
        {
            var res = NewLookup().Resolve("SQLT-BPCHK-DBCC-CHECKDB-STATUS");
            Assert.NotNull(res);
            Assert.True(res!.IsMaintenance);
            Assert.Equal(MaintenanceGenerator.CheckDb, res.Generator);
        }
    }
}
