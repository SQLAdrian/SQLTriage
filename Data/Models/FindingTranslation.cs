/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// Full translation of a single finding into three audience renderings.
    /// </summary>
    public sealed class FindingTranslation
    {
        public Guid FindingId { get; set; } = Guid.NewGuid();
        public string CheckId { get; set; } = string.Empty;
        public string CheckName { get; set; } = string.Empty;
        public string InstanceName { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public FindingDba Dba { get; set; } = new();
        public FindingItManager ItManager { get; set; } = new();
        public FindingExecutive Executive { get; set; } = new();
    }

    /// <summary>
    /// Technical rendering for the DBA audience.
    /// </summary>
    public sealed class FindingDba
    {
        public string CheckId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string TechnicalDetails { get; set; } = string.Empty;
        public string TSqlRemediation { get; set; } = string.Empty;
        public string RawData { get; set; } = string.Empty;
    }

    /// <summary>
    /// Operational rendering for the IT Manager audience.
    /// </summary>
    public sealed class FindingItManager
    {
        public string BusinessCategory { get; set; } = string.Empty;
        public string SlaImpact { get; set; } = string.Empty;
        public string RemediationEffort { get; set; } = string.Empty;
        public bool RequiresChangeControl { get; set; }
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Strategic rendering for the Executive audience.
    /// </summary>
    public sealed class FindingExecutive
    {
        public string PlainLanguageSummary { get; set; } = string.Empty;
        public string BusinessRisk { get; set; } = string.Empty;
        public string EstimatedMonthlyCost { get; set; } = string.Empty;
        public string ComplianceControls { get; set; } = string.Empty;
        public string RecommendedAction { get; set; } = string.Empty;
    }
}
