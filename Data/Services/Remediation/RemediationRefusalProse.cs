/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationRefusalProse — the ONE producer of the words an operator reads when a gate refuses a
 * fix, and the ONE producer of the words for a fix the batch never tried at all.
 *
 * WHY IT EXISTS (fix round 1, gate blocker 3). Pages/Remediation.razor's batch result rendered
 * `refused at gate ({item.Refusal})` — the RemediationRefusal enum member, straight into an
 * operator's browser, at two render sites. "NotARegisteredTemplate" and "AuditNotWritable" are
 * names for programmers. The standing idiot-proof ruling (Adrian, 2026-09-01, "make it idiot proof,
 * I will be driving it") is that a surface speaks in plain words; RemediationRollbackProse already
 * does this for the undo half, and this is its sibling for the refusal half. The sentences live in
 * a class rather than in a .razor @code block for the same reason every producer in this lane does:
 * a sentence in markup cannot be tested, and a sentence written twice drifts.
 *
 * ⚠ AND THE SECOND HALF IS NOT A REFUSAL AT ALL. An item the stop policy never reached used to be
 * carried as RemediationResult.Refused(NotApproved, ...) — a refusal enum, naming gate 4, about an
 * item nobody ever put to a gate. The operator HAD approved it; the batch simply stopped first.
 * A gate-refusal record for an unattempted item is a fabricated verdict, and "NotApproved" is the
 * most misleading one available, because it accuses the operator of the thing they did do.
 * NotAttempted() below is the honest shape, and DescribeNotAttempted names the item that stopped
 * the batch, because "why not mine?" is the only question that row raises.
 */

using System;

namespace SQLTriage.Data.Services.Remediation
{
    public static class RemediationRefusalProse
    {
        /// <summary>
        /// One gate refusal, in plain words. Never the enum member: the badge an operator reads must
        /// be a sentence they can act on, and the gate's own <see cref="RemediationResult.Message"/>
        /// carries the specifics beside it.
        /// </summary>
        public static string Describe(RemediationRefusal? refusal) => refusal switch
        {
            RemediationRefusal.NotARegisteredTemplate =>
                "this fix is not one the app is allowed to apply",
            RemediationRefusal.CapabilityDenied =>
                "this licence does not include applying fixes",
            RemediationRefusal.InsufficientCredits =>
                "there are not enough change credits left for this server",
            RemediationRefusal.NotApproved =>
                "nobody approved this change",
            RemediationRefusal.AuditNotWritable =>
                "the audit ledger could not be written, so nothing was applied",
            null => "the app refused it and did not say which check said no",
            _ => "the app refused it",
        };

        /// <summary>
        /// WHAT TO DO ABOUT one gate refusal, in the operator's own next action.
        ///
        /// <para>WHY IT EXISTS (lane/remediation-enum-prose, 2026-09-02). <see cref="Describe"/>
        /// answers "what happened" and stops there, which is the whole answer on the batch preview
        /// surface it was written for: that surface has not run anything yet. The fourteen
        /// per-operation surfaces on Pages/Remediation.razor are different. An operator has just
        /// pressed Apply on ONE fix and is looking at a red box, and "this licence does not include
        /// applying fixes" leaves them holding a fact with no move attached to it. The standing
        /// idiot-proof ruling (Adrian, 2026-09-01, "make it idiot proof, I will be driving it") is
        /// that a surface tells a person what to do next, so every member gets a next step and the
        /// pair is asserted DISTINCT across the enum by
        /// RemediationRefusalProseRenderTests.EveryRefusalMember_HasItsOwnSentenceAndItsOwnNextStep.</para>
        ///
        /// <para>⚠ It never says "nothing was sent to the server" itself. That sentence is true of
        /// every refusal and belongs to the composer below, said once, rather than repeated into six
        /// arms where one could later drift away from the others.</para>
        /// </summary>
        public static string DescribeNextStep(RemediationRefusal? refusal) => refusal switch
        {
            RemediationRefusal.NotARegisteredTemplate =>
                "Report this fix, because the app should not have offered it.",
            RemediationRefusal.CapabilityDenied =>
                "Applying fixes needs a licence that includes them. Ask whoever holds the licence.",
            RemediationRefusal.InsufficientCredits =>
                "Add change credits for this server, then run it again.",
            RemediationRefusal.NotApproved =>
                "Approve the change, then run it again.",
            RemediationRefusal.AuditNotWritable =>
                "Make the audit ledger writable, then run it again.",
            null =>
                "Report this, because the app did not name the check that said no.",
            _ =>
                "Report this, because the app did not name a check this version knows about.",
        };

        /// <summary>
        /// THE WHOLE LINE one refusal renders, produced once so fourteen surfaces cannot drift:
        /// what happened, that nothing reached the server, what to do, and the gate's own message.
        ///
        /// <para>WHY A WHOLE-LINE PRODUCER AND NOT JUST WORDS. The fourteen sites this replaces were
        /// fourteen copies of the same hand-written markup, <c>Refused at gate: @x.Refusal - @x.Message</c>,
        /// and being markup none of them could be tested and all of them drifted together only by
        /// luck. A sentence assembled in a .razor file is a sentence no assertion can reach, which is
        /// the reason every producer in this lane is a class.</para>
        ///
        /// <para>⚠ "Nothing was sent to the server" IS SAFE TO SAY OF ANY REFUSAL. A refusal is a
        /// gate answer, and all five gates are decided before the statement is executed, the audit
        /// pre-flight included. An outcome reached after execution is a
        /// <see cref="RemediationOutcome"/>, never a refusal, and it renders elsewhere.</para>
        /// </summary>
        public static string DescribeGateRefusal(RemediationRefusal? refusal, string? gateMessage)
        {
            var line = "Refused before anything ran: " + Describe(refusal)
                     + ". Nothing was sent to the server. " + DescribeNextStep(refusal);
            var detail = gateMessage?.Trim();
            return string.IsNullOrEmpty(detail) ? line : line + " " + detail;
        }

        /// <summary>
        /// What an item the batch never reached is told. It names the item that stopped the batch,
        /// because that is the only actionable fact on that row — and it is deliberately NOT a
        /// refusal: nothing about this item was ever put to a gate.
        /// </summary>
        public static string DescribeNotAttempted(string? stoppedAtKey) =>
            string.IsNullOrWhiteSpace(stoppedAtKey)
                ? "not attempted - the batch stopped before it got this far, so nothing was sent for it"
                : $"not attempted - the batch stopped at {stoppedAtKey!.Trim()}, so nothing was sent for it";
    }
}
