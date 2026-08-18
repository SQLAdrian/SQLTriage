/* In the name of God, the Merciful, the Compassionate */

// Live source: reads the Risk Assessment report straight from the connected
// instance's SQLDBA.ORG repo views. The caller selects SQLDBA.ORG as the active
// instance; we use the app's SqlServerConnectionFactory to reach it. Raw ADO.NET
// (the app has no Dapper), mirroring AlertBaselineService's open→command→reader idiom.

#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.RiskReport
{
    public sealed class LiveRiskAssessmentSource : IRiskAssessmentSource
    {
        private readonly IDbConnectionFactory _factory;
        private readonly ILogger<LiveRiskAssessmentSource> _logger;

        public LiveRiskAssessmentSource(IDbConnectionFactory factory, ILogger<LiveRiskAssessmentSource> logger)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _logger = logger;
        }

        public async Task<RiskAssessmentReport> LoadAsync(string domain, RiskReportFilters filters)
        {
            var report = new RiskAssessmentReport
            {
                Header = new RiskReportHeader { Domain = domain },
                Filters = filters,
            };

            using var conn = (SqlConnection)await _factory.CreateConnectionAsync().ConfigureAwait(false);
            // CreateConnectionAsync opens the connection (see SqlServerConnectionFactory).

            report.Header = await LoadHeaderAsync(conn, domain).ConfigureAwait(false) ?? report.Header;
            report.Rows = await LoadRowsAsync(conn, domain, filters).ConfigureAwait(false);
            return report;
        }

        private async Task<RiskReportHeader?> LoadHeaderAsync(SqlConnection conn, string domain)
        {
            // Fully database-qualified: the app's connection picker selects a server but
            // not a database (stays on master), so unqualified [dbo].[…] fails 208. The
            // views live in the SQLDBA.ORG database — address them explicitly.
            const string sql = @"
SELECT TOP 1 [domain], [Customer], [ContractType], [Last Domain Check],
       [Colour], [ServerCount], [Passed%]
FROM [SQLDBA.ORG].[dbo].[Domains_Details_Reporting]
WHERE [domain] = @domain";

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            cmd.Parameters.Add(new SqlParameter("@domain", SqlDbType.NVarChar, 75) { Value = domain });
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await r.ReadAsync().ConfigureAwait(false)) return null;

            return new RiskReportHeader
            {
                Domain = Str(r, "domain"),
                Customer = Str(r, "Customer"),
                ContractType = Str(r, "ContractType"),
                LastDomainCheck = r["Last Domain Check"] is DateTime dt ? dt.ToString("yyyy-MM-dd") : "",
                Colour = Str(r, "Colour"),
                ServerCount = IntFrom(r, "ServerCount"),
                PassedPercent = DblFrom(r, "Passed%"),
            };
        }

        private async Task<List<RiskCheckRow>> LoadRowsAsync(SqlConnection conn, string domain, RiskReportFilters f)
        {
            // SupportedOnly is cheap to push to the source; the rest are render-time visibility.
            var sql = @"
SELECT [SQLInstance], [Category], [Bad], [ImpactDescription], [ComplexityDescription],
       [FailedItems], [FailedMedium], [FailedHigh], [Weight], [Hours],
       [Section], [Details], [Summary], [OutcomeImage], [ContractDeliverable],
       [IncludedInConractType], [Supported]
FROM [SQLDBA.ORG].[dbo].[003_Checks_ConsultantTasks]
WHERE [Domain] = @domain";
            if (f.SupportedOnly) sql += " AND [Supported] = 'Yes'";

            var rows = new List<RiskCheckRow>();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
            cmd.Parameters.Add(new SqlParameter("@domain", SqlDbType.NVarChar, 75) { Value = domain });
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new RiskCheckRow
                {
                    SqlInstance = Str(r, "SQLInstance"),
                    Category = Str(r, "Category"),
                    Bad = IntFrom(r, "Bad"),
                    ImpactDescription = Str(r, "ImpactDescription"),
                    ComplexityDescription = Str(r, "ComplexityDescription"),
                    FailedItems = IntFrom(r, "FailedItems"),
                    FailedMedium = IntFrom(r, "FailedMedium"),
                    FailedHigh = IntFrom(r, "FailedHigh"),
                    Weight = DblFrom(r, "Weight"),
                    Hours = DblFrom(r, "Hours"),
                    Section = Str(r, "Section"),
                    Details = Str(r, "Details"),
                    Summary = Str(r, "Summary"),
                    OutcomeImage = Str(r, "OutcomeImage"),
                    ContractDeliverable = Str(r, "ContractDeliverable"),
                    IncludedInContractType = Str(r, "IncludedInConractType"),
                    Supported = Str(r, "Supported"),
                });
            }
            return rows;
        }

        private static string Str(IDataRecord r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";
        }
        private static int IntFrom(IDataRecord r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
        }
        private static double DblFrom(IDataRecord r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? 0d : Convert.ToDouble(r.GetValue(i));
        }
    }
}
