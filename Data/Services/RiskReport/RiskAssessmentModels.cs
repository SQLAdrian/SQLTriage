/* In the name of God, the Merciful, the Compassionate */

// Risk Assessment report — DTOs.
//
// DETACHABLE / NEVER-PUBLIC: this whole folder is Compile-Removed from the
// community build (see SQLTriage.csproj). It renders the consultant-facing
// "Your SQL Risk Assessment by SQLDBA" report (the SSRS replacement), sourced
// from the SQLDBA.ORG repo views Domains_Details_Reporting + 003_Checks_ConsultantTasks.

#nullable enable

using System.Collections.Generic;

namespace SQLTriage.Data.Services.RiskReport
{
    /// <summary>Per-customer report header — one row from <c>Domains_Details_Reporting</c>.</summary>
    public sealed class RiskReportHeader
    {
        public string Domain = "";
        public string Customer = "";
        public string ContractType = "";
        public string LastDomainCheck = "";   // formatted; rendered, not computed
        public string Colour = "";             // RAG band
        public int ServerCount;
        public double PassedPercent;           // Domains_Details_Reporting.[Passed%]
    }

    /// <summary>One finding row — from <c>003_Checks_ConsultantTasks</c>.</summary>
    public sealed class RiskCheckRow
    {
        public string SqlInstance = "";
        public string Category = "";
        public int Bad;                        // 1 = fundable bad-state
        public string ImpactDescription = "";
        public string ComplexityDescription = "";
        public int FailedItems;
        public int FailedMedium;
        public int FailedHigh;
        public double Weight;
        public double Hours;
        public string Section = "";
        public string Details = "";
        public string Summary = "";
        public string OutcomeImage = "";       // severity glyph carrier
        public string ContractDeliverable = "";
        public string IncludedInContractType = "";
        public string Supported = "";          // "Yes"/"No"
    }

    /// <summary>The four SSRS report toggles, as render-time filters.</summary>
    public sealed class RiskReportFilters
    {
        public bool SupportedOnly;
        public bool Detailed;
        public bool ShowAllChecks;
        public bool IncludeYellowChecks = true;
    }

    /// <summary>Full report payload: one header + its findings.</summary>
    public sealed class RiskAssessmentReport
    {
        public RiskReportHeader Header = new();
        public List<RiskCheckRow> Rows = new();
        public RiskReportFilters Filters = new();
    }
}
