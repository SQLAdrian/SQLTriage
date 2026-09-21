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

        /// <summary>Mirrors <see cref="CheckResult.Passed"/> — "not a failure", NOT "the control is
        /// correct". Read it together with <see cref="Verdict"/>: a WARN carries Passed=true.</summary>
        public bool Passed { get; set; }

        /// <summary>Ruling #4 (2026-07-20): the raw verdict tier, carried through so a consumer can
        /// tell a genuine PASS from a WARN riding the same Passed=true tier. Without it this DTO
        /// exposed only <see cref="Passed"/>, and any surface branching on that alone would render
        /// a check that could not assess the server as a verified pass — the defect the executive
        /// narrative in FindingTranslator had.</summary>
        public string? Verdict { get; set; }
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
