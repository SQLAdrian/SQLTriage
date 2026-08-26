/* In the name of God, the Merciful, the Compassionate */

using System;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Single source of truth for classifying a <see cref="CheckResult"/> into governance's
    /// mutually-exclusive buckets: Skip, Info, scorable-Pass, scorable-Fail. Promoted out of
    /// <see cref="GovernanceService"/> (2026-07-16) so every surface that tallies or lists
    /// results — governance, /cio, /dba, QuickCheck (audit), DailySummaryBuilder,
    /// GovernanceHistoryService — shares ONE definition instead of re-deriving it.
    ///
    /// The re-derivations had drifted: several consumers computed "Passed" straight off
    /// <see cref="CheckResult.Passed"/> without excluding INFO/SKIP first (CheckExecutionService
    /// sets Passed=true for both, so they read as counted passes), and the one place that DID
    /// filter (<see cref="GovernanceService"/>'s original IsInfo) only checked
    /// <see cref="CheckResult.Severity"/>=="INFO" — missing a verdict-contract check (e.g.
    /// SQLT-CUSTOM-MAXDOP) whose own Severity is Medium/High but whose SQL body returns an INFO
    /// verdict for a computed recommendation. That combination rode the Passed=true tier straight
    /// into every raw-Passed tally and rendered as a plain green "Pass" instead of the
    /// informational note it actually was — the defect this file closes.
    ///
    /// <see cref="GovernanceService"/> keeps its own public static IsSkip/IsInfo/IsScorable/
    /// CountsAsPass as thin forwards to these (several surfaces already reference them, some via
    /// <c>@using static SQLTriage.Data.Services.GovernanceService</c>) — the logic itself lives
    /// here, once.
    /// </summary>
    public static class CheckClassification
    {
        /// <summary>
        /// SKIP result: not applicable to this server, excluded from scoring. Honors all three
        /// signals a check can carry: the Message-prefix convention ("SKIP — ..."), an execution
        /// ErrorMessage (permission-denied etc.), AND a verdict-contract check's raw
        /// <see cref="CheckResult.Verdict"/>=="SKIP" (2026-07-16 gate-B1 fix — previously missing,
        /// so 53 of 55 Verdict-SKIP corpus results, incl. 4 Critical/24 High severity, escaped into
        /// scored Passed and rendered a green PASS badge, e.g. SQLT-CORE-00580 "No AGs configured.").
        /// </summary>
        public static bool IsSkip(CheckResult r) =>
            r.Message.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase) ||
            r.ErrorMessage != null ||
            string.Equals(r.Verdict, "SKIP", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// INFO result: informational, not a pass/fail verdict, excluded from scoring. Honors
        /// BOTH signals a check can carry: the declared <see cref="CheckResult.Severity"/>=="INFO"
        /// (numeric-contract checks — see CheckExecutionService's Severity.Equals("Info") path)
        /// AND a verdict-contract check's raw <see cref="CheckResult.Verdict"/>=="INFO" (2026-07-16
        /// fix — previously only Severity was checked, missing Verdict-INFO checks whose own
        /// declared Severity is something else entirely).
        /// </summary>
        public static bool IsInfo(CheckResult r) =>
            string.Equals(r.Severity, "INFO", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Verdict, "INFO", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// WARN result (ruling #4, 2026-07-20): the check RAN but could not fully assess the
        /// target — e.g. an under-privileged caller could not read some databases, so the
        /// work-list the check builds is incomplete. The corpus emits this from inside its
        /// <c>## Query</c> blocks by downgrading a would-be PASS
        /// (<c>WHEN (@errs &gt; 0 OR @tf_unknown = 1 OR @worklist_ok = 0) AND @__result = 'PASS'
        /// THEN 'WARN'</c>).
        ///
        /// It is NOT a server-configuration failure and must never be presented to a client as
        /// one. It is also NOT a pass: the check made no assertion about the control. Like SKIP
        /// and INFO it is therefore excluded from <see cref="IsScorable"/> — out of the score's
        /// numerator AND its denominator — so a degraded run scores honestly over what it could
        /// actually see instead of being dragged down by what it could not.
        ///
        /// Keyed on <see cref="CheckResult.Verdict"/> only. Unlike <see cref="IsSkip"/> there is
        /// no Message-prefix or ErrorMessage arm: WARN is a verdict-contract signal exclusively,
        /// and a check that genuinely errored carries ErrorMessage and is a SKIP/Error already.
        /// </summary>
        public static bool IsWarn(CheckResult r) => IsWarnVerdict(r.Verdict);

        /// <summary>
        /// <see cref="IsWarn"/> for shapes that carry a verdict string but are not a
        /// <see cref="CheckResult"/> — chiefly <c>CheckHistoryPoint</c>, the rehydrated-from-SQLite
        /// row the per-check trend computes its pass-rate from. Added with the persisted
        /// <c>check_results.verdict</c> column (ruling #4, 2026-07-20) so the trend and the live
        /// grid cannot drift apart on what counts as a WARN — one definition, two shapes.
        ///
        /// A null verdict is NOT a WARN: rows written before that column existed carry null, and
        /// the honest reading of "we never recorded the tier" is "unknown", which stays in the
        /// pre-existing Passed-based bucket rather than being retro-labelled either way.
        /// </summary>
        public static bool IsWarnVerdict(string? verdict) =>
            string.Equals(verdict, "WARN", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for a result that participates in the governance Passed/Failed math (i.e. NOT
        /// SKIP, INFO or WARN). Any surface showing Passed/Failed counts or top-findings/critical
        /// lists must filter to this subset first, or a SKIP/INFO/WARN result inflates a pass
        /// count or shows up as a "finding" it never was.
        /// </summary>
        public static bool IsScorable(CheckResult r) => !(IsSkip(r) || IsInfo(r) || IsWarn(r));

        /// <summary>
        /// F6: a client-accepted finding (accepted-by-design on this instance) rides the Passed
        /// tier so it stops dragging the score — but it stays a distinct, counted category, never
        /// a silent pass.
        /// </summary>
        public static bool CountsAsPass(CheckResult r) => r.Passed || r.IsAccepted;
    }
}
