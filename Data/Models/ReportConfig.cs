/* In the name of God, the Merciful, the Compassionate */

namespace SqlHealthAssessment.Data.Models;

/// <summary>
/// Configurable elements for the Executive Summary PDF report.
/// </summary>
public class ReportConfig
{
    // --- Branding ---
    public string CompanyName { get; set; } = "SQL Health Assessment";
    public string ReportTitle { get; set; } = "SQL Vulnerability Assessment";
    public string ReportSubtitle { get; set; } = "Executive Summary Report";
    public string PreparedBy { get; set; } = Environment.UserName;

    // --- Sections to include ---
    public bool ShowCoverPage { get; set; } = true;
    public bool ShowExecutiveSummary { get; set; } = true;
    public bool ShowSeverityBreakdown { get; set; } = true;
    public bool ShowCategoryBreakdown { get; set; } = true;
    public bool ShowDetailedFindings { get; set; } = true;
    public bool ShowPassedChecks { get; set; } = false;
    public bool ShowRecommendations { get; set; } = true;

    // --- Filters ---
    public string SeverityFilter { get; set; } = "All"; // All, Error, Warning, Information
    public string StatusFilter { get; set; } = "All";   // All, Failed, Passed
    public string CategoryFilter { get; set; } = "All";

    // --- Layout ---
    public bool LandscapeOrientation { get; set; } = false;
    public float MarginMm { get; set; } = 20f;

    // --- Colors (hex) ---
    public string PrimaryColor { get; set; } = "#1a237e";
    public string AccentColor { get; set; } = "#1565c0";
    public string CriticalColor { get; set; } = "#c62828";
    public string WarningColor { get; set; } = "#ef6c00";
    public string InfoColor { get; set; } = "#1565c0";
    public string PassColor { get; set; } = "#2e7d32";
}
