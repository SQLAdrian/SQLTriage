/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationCreditOutcome — the ONE rule for "did this apply consume a change credit", and the one
 * sentence that states it.
 *
 * Ruling 2 (DECISIONS 2026-08-25 17:33) made refunds CONFIRMED-ONLY: an inverse action that ran and
 * could not be confirmed keeps the charge, because the server's state is unknown and a refund is an
 * assertion that the change is gone.
 *
 * It lives here rather than inline in RemediationRunner because the runner CHARGES from the rule and
 * the /remediation result line TELLS THE OPERATOR about it. Two copies of a pricing rule is how a
 * page ends up promising a refund the ledger did not issue — the house lesson about artifact prose
 * being gated by the same measurement, pointed at money.
 */

namespace SQLTriage.Data.Services.Remediation
{
    public static class RemediationCreditOutcome
    {
        /// <summary>
        /// True when the change is still on the server, so the reservation commits. Verified applies
        /// stick. A verify-failure sticks unless the rollback was CONFIRMED — Unconfirmed, Failed,
        /// NotAvailable and NotAttempted all leave the change possibly live.
        /// </summary>
        public static bool ChangeStuck(RemediationOutcome outcome, RemediationRollbackState rollback) =>
            outcome == RemediationOutcome.AppliedVerified
            || (outcome == RemediationOutcome.AppliedVerifyFailed
                && rollback != RemediationRollbackState.Confirmed);

        /// <summary>
        /// What the operator reads about the money, drawn from the same predicate the runner charges
        /// from and the same cost the runner reserved. Nothing here decides anything; it reports what
        /// <see cref="ChangeStuck"/> decided, for the REAL number of credits. The lane prices applies
        /// at one to five credits (BACKUPDATABASENOW / CHECKDBNOW at three, ADDMISSINGINDEX at two),
        /// so a hard-coded "the change credit" understated every multi-credit op. The count is
        /// <paramref name="credits"/>, and the noun agrees with it (<see cref="RemediationCreditCost.Noun"/>).
        ///
        /// <para>Zero credits means no reservation was ever made — the parked-server path returns an
        /// Applied outcome without ever touching the ledger. A real reservation is always at least
        /// <see cref="RemediationCreditCost.Min"/>, so zero can only mean "nothing was charged". Say
        /// exactly that, rather than "0 change credits were refunded", which claims a refund for a
        /// charge that never happened (DECISIONS 2026-08-25, VOICE-1 parked-path ruling).</para>
        /// </summary>
        public static string DescribeCharge(
            RemediationOutcome outcome, RemediationRollbackState rollback, int credits)
        {
            if (credits <= 0) return "No change credit was charged.";

            var noun = RemediationCreditCost.Noun(credits);
            var verb = credits == 1 ? "was" : "were";
            return ChangeStuck(outcome, rollback)
                ? $"{credits} change {noun} {verb} charged."
                : $"{credits} change {noun} {verb} refunded.";
        }
    }
}
