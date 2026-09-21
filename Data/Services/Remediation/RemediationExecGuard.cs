/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationExecGuard — ROUTE A (ruled 2026-09-01, plan section 5). The second wall on the
 * remediation render/apply path.
 *
 * WHAT SPIKE S2 PROVED LIVE (section 6, and it is worse than "not blocked"):
 *   - SqlSafetyValidator.Validate returns IsSafe = TRUE for `EXEC master.sys.xp_instance_regwrite ...`,
 *     and Classify returns Safe. Safe is the classification for read-only SQL that may run on the
 *     ordinary read path with no remediation gate at all. A registry WRITE was being read as a
 *     diagnostic READ.
 *   - The shared wall's batch waiver defeats even the pattern that does exist: prefix
 *     `SELECT 1 FROM sys.databases;` and plain `xp_regwrite` classifies Safe too. A before/after
 *     capture naturally reads sys.*, so any rendered inverse would have carried its own waiver.
 *   - DangerousExecGuard catches every registry write form correctly, PER STATEMENT, and correctly
 *     allows the pure reads (xp_regread / xp_instance_regread) - and it was invoked at four
 *     production call sites, NONE of them under Data/Services/Remediation.
 *
 * WHAT THIS CLASS IS: the wiring. Every statement the remediation path renders passes through
 * DangerousExecGuard.Inspect before it is classified or executed - the rendered fix AND any rendered
 * inverse. Registry reads stay allowed (before-state capture is safe to build on); registry writes,
 * deletes and enumerations stay blocked, which is what keeps a registry inverse an honest
 * "NO ROLLBACK" marker rather than a statement nobody vetted.
 *
 * WHAT IT DELIBERATELY DOES NOT INSPECT, and why that is not a hole being papered over: the
 * VENDOR script bodies. INSTALLMAINTENANCESOLUTION ships an embedded copy of Ola Hallengren's
 * MaintenanceSolution, whose CREATE PROCEDURE bodies legitimately contain xp_fileexist and
 * friends AS PROCEDURE TEXT. Inspecting the install batch would block a shipped, gate-blessed
 * template for tokens that are data inside a CREATE PROCEDURE, not an invocation. The gate still
 * inspects that template's representative rendering (the shape the classifier vets), and the
 * install script is a checksum-pinned shipped asset, not operator input.
 * RemediationExecGuardTests pins that exemption WITH the evidence: it asserts the install text
 * really would be blocked, so the exemption is a measured decision and not an accident.
 */

using SQLTriage.Data;

namespace SQLTriage.Data.Services.Remediation
{
    public static class RemediationExecGuard
    {
        /// <summary>What is being inspected, for the refusal sentence.</summary>
        public const string RenderedFix = "rendered fix";
        public const string RenderedInverse = "rendered rollback";
        public const string RenderedSideEffectRestore = "rendered side-effect restore";

        /// <summary>
        /// Inspects one rendered batch. Returns true when nothing in it is blocked. On a refusal,
        /// <paramref name="error"/> is an operator-plain sentence naming what was refused, why, and
        /// that nothing ran.
        /// </summary>
        public static bool Allows(string? sql, string what, out string error)
        {
            var verdict = DangerousExecGuard.Inspect(sql);
            if (verdict.IsAllowed)
            {
                error = string.Empty;
                return true;
            }

            error = $"Refused before anything ran. The {what} contains an operation this app never "
                  + $"sends to a server: {verdict.Reason}. Nothing was changed and nothing was charged.";
            return false;
        }
    }
}
