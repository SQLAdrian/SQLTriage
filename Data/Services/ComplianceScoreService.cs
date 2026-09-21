/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

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
///   - When a control is flagged notTested in control_mappings.json (SQLTriage does not actually
///     test it), it is reported as "Not Tested" — un-scored and EXCLUDED from the framework
///     aggregate, so the overall score never claims coverage the tool does not have. (#87)
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
    /// <summary>
    /// Ruling #4 (2026-07-20): the status the CheckResult→AssessmentResult adapters emit for a WARN
    /// verdict. A distinct token rather than dropping the row at the adapter, so the boundary stays
    /// truthful — a future consumer of AssessmentResult sees "we could not assess this", not a
    /// silently shortened list. Scoring excludes it; see <see cref="IsUnassessed"/>.
    /// </summary>
    public const string UnassessedStatus = "Unassessed";

    /// <summary>A result that made no assertion, so it belongs in no rate. Kept as one predicate so
    /// the numerator, the denominator and FailingResults cannot drift apart on the definition.</summary>
    private static bool IsUnassessed(AssessmentResult r) =>
        string.Equals(r.Status, UnassessedStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// THE corpus <see cref="CheckResult"/> → compliance status mapping. One function, because the
    /// three adapters that need it (ReportBundleService.CorpusToAssessment, ComplianceBoard.razor
    /// and ComplianceMap.razor ToAssessmentResult) previously each carried their own copy of the
    /// ternary and were held in step only by a comment asking the next editor to remember.
    ///
    /// <para>2026-07-20 — the inflation this closes, which PREDATES the WARN slice: the tail was
    /// <c>r.Passed ? "Passed" : "Failed"</c>, and SKIP and INFO both ride Passed=true
    /// (CheckExecutionService). So a check that did not apply to this server, and a check that
    /// returned an informational note, were both tagged <b>Passed</b> in a CLIENT-FACING
    /// compliance percentage. The WARN slice fixed only the WARN arm and left its two siblings.</para>
    ///
    /// <para>Keyed on <see cref="CheckClassification.IsScorable"/> — the same predicate governance
    /// scoring uses — so all three non-assertions (WARN/SKIP/INFO) land on
    /// <see cref="UnassessedStatus"/> and drop out of the numerator AND the denominator, rather
    /// than any of them inflating the percentage or being libelled as a control breach.</para>
    /// </summary>
    public static string StatusFor(CheckResult r) =>
        !CheckClassification.IsScorable(r) ? UnassessedStatus
        : r.Passed ? "Passed"
        : "Failed";

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
            // ── Honesty gate (#87): a control SQLTriage does not actually test is rendered
            // un-scored and kept OUT of the aggregate — never scored off unrelated generic
            // checks. Must be evaluated before any scoring so it cannot leak into the total.
            if (cat.NotTested)
            {
                familyScores.Add(new ControlFamilyScore
                {
                    FamilyId = cat.Id,
                    FamilyName = cat.Name,
                    TotalChecks = 0,
                    PassingChecks = 0,
                    Percent = -1,   // -1 = un-scored sentinel (shared with NoData)
                    Status = ControlStatus.NotTested,
                    NotTestedReason = cat.NotTestedReason,
                    FailingResults = Array.Empty<AssessmentResult>(),
                });
                continue;
            }

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

            // Ruling #4 (2026-07-20): a WARN result asserted NOTHING about this control — the check
            // ran but could not fully assess the target. Drop it before any counting so it is out of
            // the numerator AND the denominator, exactly as CheckClassification.IsScorable treats it
            // upstream. Both alternatives are wrong in a client-facing scorecard: counting it as
            // Passed inflated the family (measured: a {Passed, Failed} control scored 50.0%, and the
            // same control with one WARN added scored 66.67% — less visibility, better compliance
            // score); counting it as not-Passed would report a permissions gap as a compliance
            // failure and put it in FailingResults as audit evidence of a control breach it is not.
            var unassessed = relevant.Count(IsUnassessed);
            relevant = relevant.Where(r => !IsUnassessed(r)).ToList();

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
                    FailingResults = Array.Empty<AssessmentResult>(),
                    // A control whose ONLY results were unassessable is NoData — honest: nothing
                    // was established either way. The count says why it is empty, so "no data"
                    // cannot be misread as "nothing was mapped to this control".
                    UnassessedChecks = unassessed,
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
                // ALL failing findings for this control — NOT capped (#87). Exported audit
                // evidence must be complete; the UI applies its own display cap at render time.
                FailingResults = relevant
                    .Where(r => !string.Equals(r.Status, "Passed", StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                UnassessedChecks = unassessed,
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

    /// <summary>
    /// The scorecard as the exported PDF carries it: one projection from what was scored to what a
    /// client reads as audit evidence.
    ///
    /// <para>WHY IT IS HERE AND NOT IN THE PAGE (2026-08-27 fix round, reports-r1-07). This mapping
    /// lived inline in <c>Pages/ComplianceMap.razor</c>'s export handler, which nothing in the test
    /// project can construct. So the r1-07 fix — copying <see cref="ControlFamilyScore.UnassessedChecks"/>
    /// onto the exported row — sat on a razor line with no test behind it. The gate proved the hole
    /// by deleting that single line: the suite stayed green, 5466 passed, while the client PDF
    /// silently lost its "Not assessed" column and its partial-coverage sentence. A fix nothing
    /// exercises is one careless edit from being un-fixed. Moved here, the projection is ordinary
    /// code with an ordinary test, and the page keeps only what is genuinely page-shaped: the meta
    /// block, the file write, the audit line and the toast.</para>
    ///
    /// <para>Pure: no IO, no injected service, no page state. <paramref name="meta"/> is built by the
    /// caller because it carries operator settings (company, watermark, timezone) this class has no
    /// business knowing.</para>
    /// </summary>
    public static ComplianceReport ToExportModel(ComplianceScorecard scorecard, AssessmentMeta meta)
    {
        ArgumentNullException.ThrowIfNull(scorecard);

        var report = new ComplianceReport
        {
            Meta = meta,
            OverallPercent = scorecard.OverallPercent,
            Compliant = scorecard.FamilyScores.Count(f => f.Status == ControlStatus.Compliant),
            Partial = scorecard.FamilyScores.Count(f => f.Status == ControlStatus.PartiallyCompliant),
            NonCompliant = scorecard.FamilyScores.Count(f => f.Status == ControlStatus.NonCompliant),
            OutsideScope = scorecard.FamilyScores.Count(f => f.Status == ControlStatus.NoData),
            NotTested = scorecard.FamilyScores.Count(f => f.Status == ControlStatus.NotTested),
        };

        foreach (var f in scorecard.FamilyScores)
        {
            var row = new ComplianceFamilyRow
            {
                Id = f.FamilyId,
                Name = f.FamilyName,
                Percent = f.Percent,
                Status = f.Status.ToString(),
                NotTested = f.Status == ControlStatus.NotTested,
                NotTestedReason = f.NotTestedReason,
                // reports-r1-07 (2026-08-27): the web page has rendered this as a "Not assessed"
                // column since ruling #4 and the export dropped it, so the PDF — the copy that
                // leaves the building as audit evidence — was the one deliverable presenting a
                // partial rate with no way to tell it was partial.
                UnassessedChecks = f.UnassessedChecks,
            };

            // Complete, uncapped failing evidence per control (#87) — the exported PDF must carry
            // every failed finding, not a 5-row sample.
            foreach (var r in f.FailingResults)
                row.Findings.Add(new ComplianceFindingRow
                {
                    CheckId = r.CheckId,
                    Name = r.DisplayName,
                    Severity = r.Severity,
                    Message = r.Message,
                    Server = !string.IsNullOrWhiteSpace(r.ThisServer) ? r.ThisServer!
                           : !string.IsNullOrWhiteSpace(r.TargetName) ? r.TargetName : "(unspecified)",
                });

            report.Families.Add(row);
        }

        return report;
    }

    // ── Data models ─────────────────────────────────────────────────────────

    public sealed class ComplianceScorecard
    {
        public string Framework { get; init; } = "";
        /// <summary>-1 when no VA data available.</summary>
        public double OverallPercent { get; init; }
        public List<ControlFamilyScore> FamilyScores { get; init; } = new();
        public DateTime ComputedAt { get; init; }

        // A NotTested family is not "data" — it is a permanent honesty placeholder that exists
        // even with zero VA results, so it must not flip the scorecard into its scored view (#87).
        public bool HasData => FamilyScores.Any(f =>
            f.Status != ControlStatus.NoData && f.Status != ControlStatus.NotTested);
    }

    public sealed class ControlFamilyScore
    {
        public string FamilyId { get; init; } = "";
        public string FamilyName { get; init; } = "";
        public int TotalChecks { get; init; }
        public int PassingChecks { get; init; }
        /// <summary>-1 = un-scored (No Data, or Not Tested).</summary>
        public double Percent { get; init; }
        public ControlStatus Status { get; init; }
        /// <summary>Honest explanation shown when <see cref="Status"/> is <see cref="ControlStatus.NotTested"/>.</summary>
        public string NotTestedReason { get; init; } = "";
        /// <summary>ALL failing findings mapped to this control — NOT capped (#87). Consumers that need a
        /// short list (e.g. the UI drill-down) apply their own display cap; exported evidence uses the full set.</summary>
        public IReadOnlyList<AssessmentResult> FailingResults { get; init; } = Array.Empty<AssessmentResult>();

        /// <summary>
        /// Ruling #4 (2026-07-20): checks mapped to this control that could not fully assess the
        /// target (WARN), and are therefore in NEITHER <see cref="TotalChecks"/> nor
        /// <see cref="PassingChecks"/> nor <see cref="FailingResults"/>. Non-zero means
        /// <see cref="Percent"/> was computed over less than the whole control — the number exists
        /// so a consumer can say so out loud instead of presenting a partial rate as a full one.
        /// </summary>
        public int UnassessedChecks { get; init; }
    }

    // NotTested appended last so existing ToString()/switch consumers are unaffected.
    public enum ControlStatus { NoData, Compliant, PartiallyCompliant, NonCompliant, NotTested }
}
