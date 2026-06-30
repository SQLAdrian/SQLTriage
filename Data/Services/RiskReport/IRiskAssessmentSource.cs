/* In the name of God, the Merciful, the Compassionate */

// Data seam for the Risk Assessment report. LiveRiskAssessmentSource queries the
// connected SQLDBA.ORG instance now; an offline-artifact implementation can be
// dropped in later (matching the HealthBenchmark extractor pattern) without the
// builder/service knowing the difference.

#nullable enable

using System.Threading.Tasks;

namespace SQLTriage.Data.Services.RiskReport
{
    public interface IRiskAssessmentSource
    {
        /// <summary>
        /// Load the header + findings for one customer domain, applying the report
        /// filters at the source where cheap (e.g. SupportedOnly). Returns an empty
        /// report (header with the domain, no rows) when the domain is unknown.
        /// </summary>
        Task<RiskAssessmentReport> LoadAsync(string domain, RiskReportFilters filters);
    }
}
