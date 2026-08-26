/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationAcknowledgements — turns the acknowledgement parameters on a request into the sentence
 * the audit ledger records with the approval.
 *
 * Rulings 3 and 7 (DECISIONS 2026-08-25 17:33) each opened ONE route past an absolute refusal: a
 * forced Maintenance Solution install over objects that already exist, and a deliberate move off a
 * recommended value. Both were ruled the same way — the operator states intent, the consequence is
 * spelled out on screen, and the action plus the acknowledgement are logged. This is the logging
 * half, in one place, so a third route added later cannot quietly ship without one.
 *
 * Reads only. Decides nothing: each route's own gate (MaintenanceSolutionOpRenderer.
 * IsOverwriteAcknowledged, RemediationOpRenderer.IsRegressionAcknowledged) still owns whether the
 * write is allowed, and both fail closed. This describes what those gates saw.
 */

using System.Collections.Generic;

namespace SQLTriage.Data.Services.Remediation
{
    public static class RemediationAcknowledgements
    {
        /// <summary>
        /// Every acknowledgement carried by <paramref name="parameters"/>, as one sentence per
        /// route, or null when there are none. Null rather than empty so the runner passes the
        /// audit ledger the same "no details" it always did for an ordinary apply.
        /// </summary>
        public static string? Describe(IReadOnlyDictionary<string, string>? parameters)
        {
            if (parameters is null || parameters.Count == 0) return null;

            var lines = new List<string>();

            if (RemediationOpRenderer.IsRegressionAcknowledged(parameters))
            {
                // The intent is guaranteed non-blank here: IsRegressionAcknowledged requires it.
                lines.Add("Off-recommended change acknowledged. The operator asked to move this "
                          + "setting off its recommended value. Stated reason: "
                          + RemediationOpRenderer.ReadRegressionIntent(parameters));
            }

            if (MaintenanceSolutionOpRenderer.IsOverwriteAcknowledged(parameters))
            {
                lines.Add("Forced install acknowledged. The operator asked to install over "
                          + "Maintenance Solution objects that already exist. It overwrites the "
                          + "existing objects and is not reversible.");
            }

            return lines.Count == 0 ? null : string.Join(" ", lines);
        }

        /// <summary>
        /// True when this request carries any acknowledged override. Used where a call site needs
        /// the fact rather than the sentence.
        /// </summary>
        public static bool Any(IReadOnlyDictionary<string, string>? parameters) =>
            Describe(parameters) is not null;
    }
}
