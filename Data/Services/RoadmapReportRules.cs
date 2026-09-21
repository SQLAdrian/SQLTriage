/* In the name of God, the Merciful, the Compassionate */
/*
 * RoadmapReportRules — the rules the Diagnostics Maturity Roadmap prints by.
 *
 * WHY THIS FILE EXISTS. The level status, the maturity ladder and the severity banding were each
 * computed more than once: once for the client PDF, once for the Action Plan CSV, once again for
 * the per-server table, and a fourth time in a doc comment. They drifted. With pass rates
 * {L1 95, L2 70, L3 88, L4 85, L5 92} the same run stamped with the same run id printed L5 as
 * COMPLETE in the PDF and NOT STARTED in the CSV. That is the same "more than one copy of one
 * rule" shape that let the sp_Blitz CSV layout drift out from under two of three parsers.
 * One rule, one place, and every artefact is a projection of it.
 *
 * THE STATUS VOCABULARY, and why it changed. The old words asserted work state nobody measured.
 * "COMPLETE" claimed a level was finished while its own row printed open failures next to it, and
 * "NOT STARTED" claimed no work had happened on a level the same row scored at 92%. The three
 * words below each name a measurement the reader can check against the numbers beside them:
 *   TARGET MET   — this level's pass rate reached its target.
 *   BELOW TARGET — this level was measured and did not reach it.
 *   NOT ASSESSED — this level has no checks in the universe, so no claim is made.
 * A status that contradicts the number printed next to it is the defect; the words are the fix.
 */

#nullable enable

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SQLTriage.Data.Services;

public static class RoadmapReportRules
{
    /// <summary>Pass rate a maturity level must reach to count as met.</summary>
    public const int DefaultTargetPercent = 80;

    public const string StatusTargetMet   = "TARGET MET";
    public const string StatusBelowTarget = "BELOW TARGET";
    public const string StatusNotAssessed = "NOT ASSESSED";

    /// <summary>Pass rate as a percentage. A level with no checks has no pass rate; it reads 0.</summary>
    public static double PassRate(int passedCount, int totalCount)
        => totalCount > 0 ? passedCount * 100.0 / totalCount : 0.0;

    /// <summary>
    /// True when the level was assessed AND reached its target. A level with no checks is NOT met:
    /// an unassessed level is not evidence of maturity, and the two copies of this rule disagreed
    /// about exactly that (one scored an empty level 0%, the other 100%, so the same data produced
    /// two different maturity levels on two surfaces of one report).
    /// </summary>
    public static bool TargetMet(int passedCount, int totalCount, int targetPercent = DefaultTargetPercent)
        => totalCount > 0 && PassRate(passedCount, totalCount) >= targetPercent;

    /// <summary>The one status string every artefact prints. See the file header for the vocabulary.</summary>
    public static string LevelStatus(int passedCount, int totalCount, int targetPercent = DefaultTargetPercent)
        => totalCount <= 0
            ? StatusNotAssessed
            : TargetMet(passedCount, totalCount, targetPercent) ? StatusTargetMet : StatusBelowTarget;

    /// <summary>
    /// Highest level whose target is met with every level below it also met. 0 when the first level
    /// is not met, which is the honest reading of a server that fails Foundation outright.
    ///
    /// <para>The PDF cover's progress dots used to fill from <c>OverallLevel</c>, which is the level
    /// currently being worked on and is clamped to a floor of 1 — so a server failing L1 at 0%
    /// still printed a filled Foundation dot beside an Executive Summary row that said the same
    /// level had not reached its target. The dots read "achieved through"; this is that number.</para>
    /// </summary>
    public static int AchievedLevel(IReadOnlyList<(int PassedCount, int TotalCount, int TargetPercent)> levels)
    {
        var achieved = 0;
        for (var i = 0; i < levels.Count; i++)
        {
            var (passed, total, target) = levels[i];
            if (!TargetMet(passed, total, target)) break;
            achieved = i + 1;
        }
        return achieved;
    }

    /// <summary>
    /// The stage an operator is working on: one above the highest achieved, floored at 1 and capped
    /// at 5. Kept distinct from <see cref="AchievedLevel"/> on purpose — the badge and the dots mean
    /// different things and reading one variable as both is what audit-r2-05 filed.
    /// </summary>
    public static int CurrentStage(int achievedLevel)
        => achievedLevel + 1 < 1 ? 1 : (achievedLevel + 1 > 5 ? 5 : achievedLevel + 1);

    // ── Severity banding ─────────────────────────────────────────────────────
    // The code bucketed L1+L2 as Critical, L3 as High, L4+L5 as Medium and weighted L2 at 4, while
    // the comment two lines above claimed Critical was L1 only, High was L2, Medium was L3+, and L2
    // weighed 2. The screen tooltips agreed with the code, so the comment was the outlier and the
    // client-facing chips explained neither. Both now read from here, and the caption ships with the
    // numbers so a reader can see what a "Critical" count counts.

    public static string SeverityBand(int level) => level switch
    {
        1 or 2 => "Critical",
        3      => "High",
        _      => "Medium",
    };

    /// <summary>Risk weight per maturity level, used for the risk-weighted score.</summary>
    public static int RiskWeight(int level) => level switch
    {
        1 or 2 => 4,
        3      => 2,
        _      => 1,
    };

    /// <summary>Highest weight any level carries; the denominator's per-check multiplier.</summary>
    public const int MaxRiskWeight = 4;

    /// <summary>Printed with the severity counts so the counts are readable without the source.</summary>
    public const string SeverityBandCaption =
        "Critical counts Level 1 and Level 2 findings. High counts Level 3. Medium counts Level 4 and Level 5.";

    // ── Per-check action text ────────────────────────────────────────────────

    /// <summary>
    /// An unresolved template token such as &lt;Owner&gt; left in the mapping's next_action text.
    /// </summary>
    private static readonly Regex UnresolvedPlaceholder = new(@"<[A-Za-z][A-Za-z0-9 _/-]*>", RegexOptions.Compiled);

    public static bool HasUnresolvedPlaceholder(string? text)
        => !string.IsNullOrWhiteSpace(text) && UnresolvedPlaceholder.IsMatch(text);

    /// <summary>
    /// The per-check action to print, or null when there is none to print.
    ///
    /// <para>Config/roadmap-mapping.json carries next_action on 18 of its 334 entries and 14 of
    /// those still hold an unresolved &lt;Owner&gt; token. Printing template scaffolding into a
    /// client PDF is not a recommendation, so an entry that still carries one is treated as
    /// incomplete source data and no action is printed for it. The caller counts the suppressions
    /// and logs them, so the gap is visible to the operator rather than swallowed.</para>
    /// </summary>
    public static string? PerCheckAction(string? nextAction)
    {
        if (string.IsNullOrWhiteSpace(nextAction)) return null;
        if (HasUnresolvedPlaceholder(nextAction)) return null;
        // The source stores multi-step actions tab-separated; a single line reads better in a cell.
        return Regex.Replace(nextAction.Replace('\t', ' ').Trim(), @"\s{2,}", " ");
    }
}
