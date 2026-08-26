/* In the name of God, the Merciful, the Compassionate */
/*
 * CheckUniverse — the ONE rule for "can this BlitzCheckID fire on an audited instance at all".
 *
 * WHY THIS EXISTS, and why it is one file rather than a guard per consumer.
 *
 * sp_Blitz emits a CSV row only when a check FIRES. Passes are never observed, so every scoring
 * surface in this app SYNTHESIZES a universe from a catalog and treats "in the universe, not in the
 * fired set" as a PASS. That inference is only true for checks the deployed script can actually
 * emit. An id that no deployed script can emit is a PERMANENT free pass: it lands in the numerator
 * on every instance, on every run, forever, and it renders to the client as a check that was
 * assessed and found healthy. It was not assessed at all.
 *
 * Until 2026-08-26 there were TWO universes with TWO different guards. BlitzDashboardService had
 * this rule (a retired-id set plus a sentinel floor) and Pages/DiagnosticsRoadmap.razor had none, so
 * the CLIENT-FACING PDF scored ids the operator-facing dashboard had already excluded. That is the
 * "more than one copy of one rule" shape that produced audit-r1-06. One predicate, both callers.
 *
 * WHAT IS IN HERE IS MEASURED, NOT ASSUMED. FrkContractTests derives the never-fires set from the
 * vendored scripts/sp_Blitz.sql and asserts it equals this set exactly, so an FRK bump that revives
 * or retires an id fails the build instead of silently moving a score.
 *
 * ⚠ ADD AN ID ONLY WITH THE MEASUREMENT THAT PUT IT HERE, named in the comment beside it. An id
 * dropped in without that provenance is indistinguishable from a check someone wanted to stop
 * grading, and stopping grading a LIVE check is the worse of the two failures: the score goes UP
 * and nothing says so.
 */

#nullable enable

using System.Collections.Generic;

namespace SQLTriage.Data.Services;

/// <summary>
/// Membership rule for the sp_Blitz scoring universe, shared by the dashboard and the Compliance
/// Roadmap (and therefore by the client PDF and the Action Plan CSV, which are projections of the
/// roadmap's numbers).
/// </summary>
public static class CheckUniverse
{
    /// <summary>
    /// BlitzCheckIDs at or above this are NOT sp_Blitz checks. They are the sp_triage custom checks
    /// (CPU saturation, weak passwords, power plan, …) that carry the sentinel 9999 in the
    /// AllCheckTable because they have no upstream sp_Blitz id. Real sp_Blitz ids are 1..~275.
    /// </summary>
    public const int SentinelIdFloor = 9000;

    /// <summary>
    /// CheckIDs the DEPLOYED First Responder Kit no longer emits.
    ///
    /// <para>RETIRED BY FRK 20260708 (the release this repo vendors as 8.34 / 20260702):
    /// <b>129</b> "Dangerous Build of SQL Server (Corruption)" and <b>157</b> "Dangerous Build of SQL
    /// Server (Security)". Their check bodies are gone from scripts/sp_Blitz.sql; the only surviving
    /// mention of either id is the skip guard <c>CheckID IN (128, 129, 157, 189, 216)</c>. Both
    /// matched SQL build ranges from 2008 through 2014, which the 20260407 support floor of SQL 2016
    /// SP2 put out of reach. Pinned by
    /// FrkContractTests.CheckIDs_129_and_157_are_retired_and_only_survive_inside_a_skip_guard.</para>
    /// </summary>
    public static readonly IReadOnlySet<int> RetiredCheckIds = new HashSet<int> { 129, 157 };

    /// <summary>
    /// CheckIDs that sit in Config/roadmap-mapping.json but that no vendored script emits, so they
    /// are neither retired nor live: they were never reachable from this app's scripts at all.
    ///
    /// <para>HOW THIS SET WAS DERIVED, 2026-08-26. Take the roadmap mapping's scoring universe (310
    /// ids: level 1..5, category not Server Info / Information / Informational). Subtract every id
    /// the vendored scripts/sp_Blitz.sql can textually emit, measured by
    /// FrkContractTests.EmittedCheckIds over its seven syntactic forms. What is left, below the
    /// sentinel floor and outside <see cref="RetiredCheckIds"/>, is exactly these six.</para>
    ///
    /// <list type="bullet">
    /// <item><b>9</b> "Endpoints Configured, Review Required" (L3 Security)</item>
    /// <item><b>38</b> "Heap Tables. No Clustered Index" (L5 Performance)</item>
    /// <item><b>39</b> "Possible Stale Backup/Shadow Tables" (L4 Performance)</item>
    /// <item><b>52</b> "Cluster Failover History Detected" (L2 Configuration)</item>
    /// <item><b>127</b> "Cumulative Update Available" (L2 Reliability). The LIVE id for that same
    /// check is <b>217</b>, which sp_Blitz does emit. With 127 in the universe one L2 Reliability
    /// card could print "Cumulative Update Available" twice: once as a green tick (127, never fires)
    /// and once as a red cross (217, fired).</item>
    /// <item><b>999</b> "Third-Party Backup Tool Detected, Native Backup History Missing" (L3
    /// Reliability). sp_Blitz has no id 999. sp_triage emits a row numbered 999, but it is the
    /// SECTION SEPARATOR <c>SELECT 999, 'Blitz from here','------','------'</c>
    /// (scripts/SQLDBA.ORG.sp_triage.sql), not the check the mapping names. Under the older
    /// sp_triage shape the scanner drops it already, because its Section label does not start
    /// "sp_Blitz:"; under the new sqlmagic shape (SectionID taken directly) it does not, and a
    /// separator was being counted as a fire of a backup check. Excluded on both sides: the mapped
    /// check can never fire, and a 999 row is a separator rather than a finding.</item>
    /// </list>
    ///
    /// <para>THE ORDER THIS WAS FIXED IN MATTERS. The derivation is only trustworthy because
    /// FrkContractTests.CheckIdFormA was corrected first to see the quoted-literal form
    /// <c>'199' AS CheckID</c>. Before that correction CheckID 199 ("There Is An Error With The
    /// Default Trace") also read as never-emitted, and putting a LIVE check in this set would have
    /// silently stopped grading it. Measured on the 2026-08-26 live probe of <c>.\OLD2017</c>:
    /// excluding 199, which PASSED there, reads 277/300 = 92.33% against the honest 278/301 =
    /// 92.36%, so the score falls. It rises only where the excluded check fires. Either direction
    /// is the same defect, and the first way this was written ("raised every score") named only
    /// half of it.</para>
    /// </summary>
    public static readonly IReadOnlySet<int> UnreachableCheckIds =
        new HashSet<int> { 9, 38, 39, 52, 127, 999 };

    /// <summary>
    /// True when <paramref name="checkId"/> is an id a deployed script can emit, and therefore an id
    /// whose ABSENCE from a fired set is real evidence of a pass.
    ///
    /// <para>Apply it in BOTH directions, which is what makes it a universe filter rather than a
    /// display filter: an excluded id never enters the universe (so it is never a pass, never a
    /// scored denominator member, and never rendered as an assessed check), and an excluded id
    /// arriving in a FIRED set is ignored rather than falling through to an unknown-id branch that
    /// would turn it into a nameless weight-1 ding.</para>
    /// </summary>
    public static bool CanFire(int checkId) =>
        checkId > 0
        && checkId < SentinelIdFloor
        && !RetiredCheckIds.Contains(checkId)
        && !UnreachableCheckIds.Contains(checkId);

    /// <summary>
    /// Why an id is outside the universe, in one operator-readable clause, or null when it is inside.
    /// For log lines and diagnostics only. Never a client-facing string.
    /// </summary>
    public static string? WhyItCannotFire(int checkId)
    {
        if (checkId <= 0) return "not a check id (sp_Blitz uses -1 for its banner row)";
        if (checkId >= SentinelIdFloor) return "an sp_triage custom-check sentinel, not an sp_Blitz check";
        if (RetiredCheckIds.Contains(checkId)) return "retired by FRK 20260708; the deployed script cannot emit it";
        if (UnreachableCheckIds.Contains(checkId)) return "no vendored script emits this id";
        return null;
    }
}
