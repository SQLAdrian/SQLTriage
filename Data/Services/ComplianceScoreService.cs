/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services;

// BM:ComplianceScoreService.Class — scores VA results against compliance framework controls
/// <summary>
/// Computes a compliance scorecard for a given framework against the currently loaded VA results.
/// Scoring logic:
///   - Each framework control (category) declares one or more sqlCheckHints.
///   - Each sqlCheckHint maps to one or more VA result Categories (via ComplianceMappingService.CategoryToHints).
///   - A VA result "passes" a control when: the result.Status == "Passed" AND result.Category maps
///     to a hint that this control declares.
///   - A VA result "fails" a control when: the result.Status != "Passed" AND the same mapping holds.
///   - Control compliance % = distinct passing categories / all relevant categories for that control.
///   - When no VA results map to a control, it is reported as "No Data" (not scored).
/// </summary>
public sealed class ComplianceScoreService
{
    private readonly ILogger<ComplianceScoreService> _logger;
    private readonly ComplianceMappingService _mapping;

    public ComplianceScoreService(ILogger<ComplianceScoreService> logger, ComplianceMappingService mapping)
    {
        _logger = logger;
        _mapping = mapping;
    }

    /// <summary>
    /// Computes a full scorecard for the given framework acronym using the supplied VA results.
    /// Pass <c>State.Results</c> from <see cref="VulnerabilityAssessmentStateService"/>.
    /// </summary>
    public ComplianceScorecard ComputeScorecard(IReadOnlyList<AssessmentResult> results, string framework)
    {
        var categories = _mapping.GetCategoriesForFramework(framework);

        // Pre-index VA results by Category for O(1) lookup
        var byCategory = results
            .GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var familyScores = new List<ControlFamilyScore>();
        int overallPassing = 0, overallTotal = 0;

        foreach (var cat in categories)
        {
            // Collect all VA category names that map to any hint this control declares
            var relevantVaCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hint in cat.SqlCheckHints)
            {
                foreach (var kv in ComplianceMappingService.CategoryToHints)
                    if (kv.Value.Contains(hint, StringComparer.OrdinalIgnoreCase))
                        relevantVaCategories.Add(kv.Key);
            }

            // Gather all VA results that map to this control
            var relevant = new List<AssessmentResult>();
            foreach (var vacat in relevantVaCategories)
                if (byCategory.TryGetValue(vacat, out var list))
                    relevant.AddRange(list);

            if (relevant.Count == 0)
            {
                familyScores.Add(new ControlFamilyScore
                {
                    FamilyId = cat.Id,
                    FamilyName = cat.Name,
                    TotalChecks = 0,
                    PassingChecks = 0,
                    Percent = -1,   // -1 = No Data sentinel
                    Status = ControlStatus.NoData,
                    SampleResults = Array.Empty<AssessmentResult>(),
                });
                continue;
            }

            int passing = relevant.Count(r => string.Equals(r.Status, "Passed", StringComparison.OrdinalIgnoreCase));
            int total = relevant.Count;
            double pct = total > 0 ? passing * 100.0 / total : 0;

            overallPassing += passing;
            overallTotal += total;

            familyScores.Add(new ControlFamilyScore
            {
                FamilyId = cat.Id,
                FamilyName = cat.Name,
                TotalChecks = total,
                PassingChecks = passing,
                Percent = pct,
                Status = pct >= 80 ? ControlStatus.Compliant
                              : pct >= 50 ? ControlStatus.PartiallyCompliant
                              : ControlStatus.NonCompliant,
                SampleResults = relevant
                    .Where(r => !string.Equals(r.Status, "Passed", StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .ToArray(),
            });
        }

        double overall = overallTotal > 0 ? overallPassing * 100.0 / overallTotal : -1;

        _logger.LogDebug(
            "ComplianceScorecard for {Framework}: {Passing}/{Total} = {Pct:F1}% across {Families} control families",
            framework, overallPassing, overallTotal, overall, familyScores.Count);

        return new ComplianceScorecard
        {
            Framework = framework,
            OverallPercent = overall,
            FamilyScores = familyScores,
            ComputedAt = DateTime.UtcNow,
        };
    }

    // ── Data models ─────────────────────────────────────────────────────────

    public sealed class ComplianceScorecard
    {
        public string Framework { get; init; } = "";
        /// <summary>-1 when no VA data available.</summary>
        public double OverallPercent { get; init; }
        public List<ControlFamilyScore> FamilyScores { get; init; } = new();
        public DateTime ComputedAt { get; init; }

        public bool HasData => FamilyScores.Any(f => f.Status != ControlStatus.NoData);
    }

    public sealed class ControlFamilyScore
    {
        public string FamilyId { get; init; } = "";
        public string FamilyName { get; init; } = "";
        public int TotalChecks { get; init; }
        public int PassingChecks { get; init; }
        /// <summary>-1 = No Data (no VA results map to this control).</summary>
        public double Percent { get; init; }
        public ControlStatus Status { get; init; }
        public IReadOnlyList<AssessmentResult> SampleResults { get; init; } = Array.Empty<AssessmentResult>();
    }

    public enum ControlStatus { NoData, Compliant, PartiallyCompliant, NonCompliant }
}
