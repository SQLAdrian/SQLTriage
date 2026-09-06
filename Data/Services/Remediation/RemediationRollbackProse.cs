/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationRollbackProse — the ONE producer of every sentence an operator reads about undo:
 * whether a fix is reversible BEFORE they approve it, and what happened to a rollback AFTER it ran.
 *
 * WHY IT IS ONE FILE (Phase-2 items 6b and 6d, Adrian 2026-09-01).
 *
 *   6b - reversibility was only ever knowable AFTER the fact. RemediationRollbackState is a
 *        post-apply field; nothing told the operator, at the moment of approving, whether this
 *        particular fix on this particular server could be put back. A fix that cannot be undone is
 *        the single most important thing to know before clicking, and it was the one thing the
 *        preview did not say. The marker is now produced here and rides the preview text, so it
 *        reaches BOTH the single-fix surface and the batch surface without either page restating it.
 *
 *   6d - the post-apply states had honest ENUM VALUES and lost their honest ERROR STRINGS. The
 *        executor writes a real sentence into RollbackError ("The rollback ran without error. The
 *        confirming read then failed, so this server's current state is unknown..."), and the page's
 *        Unconfirmed and Failed arms rendered a fixed phrase and dropped it. The operator was told
 *        "check the server" and never told what the app had actually seen.
 *
 * The sentences live here rather than in a .razor @code block for the reason every producer in this
 * lane does: a sentence in markup cannot be tested, and a sentence written twice drifts.
 */

using System;
using SQLTriage.Data.Services;

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>
    /// What an operator can be told about undo BEFORE approving. <see cref="CanRollBack"/> is
    /// deliberately nullable: null is "we do not know", which is neither a promise nor a refusal.
    /// </summary>
    public sealed record RemediationReversibility(bool? CanRollBack, string Sentence);

    public static class RemediationRollbackProse
    {
        /// <summary>
        /// The marker that opens every honest "this cannot be undone" sentence. One constant, so a
        /// surface can RECOGNISE the state as well as print it, and a test can assert the operator
        /// sees the words rather than a boolean.
        /// </summary>
        public const string NoRollbackMarker = "NO ROLLBACK:";

        /// <summary>Prefix of the affirmative sentence, for the same reason.</summary>
        public const string ReversibleMarker = "Reversible:";

        /// <summary>
        /// The ONE way anything in this codebase says "nothing was run to put this back, and here is
        /// why". Phase 3 added three more places that needed to say it — the batch driver, the runner's
        /// undo gates, and the executor's undo refusals — and three hand-written
        /// <c>NoRollbackMarker + " " + reason</c> concatenations is how a marker a surface RECOGNISES
        /// stops being reliable (one stray missing space and a page's state detection silently fails).
        /// </summary>
        public static string NoRollback(string reason) =>
            $"{NoRollbackMarker} {(reason ?? string.Empty).Trim()}";

        /// <summary>
        /// Reversibility of one Configuration (sp_configure) fix on THIS server, given the configured
        /// value the preview just read. A value that could not be read is the honest no: there is
        /// nothing to put back, and saying "reversible" over an unread pre-state is the exact
        /// over-claim the 5-state rollback enum exists to prevent.
        /// </summary>
        public static RemediationReversibility ForConfiguration(
            RemediationTemplate? template, string? configName, int? configuredValue)
        {
            var name = string.IsNullOrWhiteSpace(configName) ? "this setting" : $"'{configName}'";

            if (template is not null && !template.Reversible)
                return new RemediationReversibility(false,
                    $"{NoRollbackMarker} this fix is not reversible. Once it is applied, the app cannot put it back.");

            if (configuredValue is not int current)
                return new RemediationReversibility(false,
                    $"{NoRollbackMarker} the current configured value of {name} could not be read, "
                    + "so there is nothing to put back if the change does not verify.");

            return new RemediationReversibility(true,
                $"{ReversibleMarker} if this change does not verify, {name} is set back to {current} "
                + "and the app reads the server back to confirm it.");
        }

        /// <summary>
        /// Reversibility of one per-database (ALTER DATABASE ... SET) fix. Only a simple ON/OFF
        /// toggle has an inverse the app can derive from membership of the offenders query; a
        /// compound clause does not, and the executor already refuses to fabricate one.
        /// </summary>
        public static RemediationReversibility ForDbSetOption(RemediationTemplate? template, string? optionSql)
        {
            if (template is not null && !template.Reversible)
                return new RemediationReversibility(false,
                    $"{NoRollbackMarker} this fix is not reversible. Once it is applied, the app cannot put it back.");

            if (RemediationOpRenderer.TryInvertBooleanOptionSql(optionSql ?? string.Empty, out var inverse))
                return new RemediationReversibility(true,
                    $"{ReversibleMarker} if this change does not verify, every database it changed is set back "
                    + $"with {inverse} and the app re-runs its own check to confirm it.");

            // ⚠ THE OLD SENTENCE LED WITH THE MECHANISM (lane/remediation-enum-prose-2, 2026-09-02).
            // It read "this database option is not a simple ON or OFF toggle, so there is no inverse
            // to run", which is true and is written from the app's side of the glass: it tells an
            // operator what SHAPE the option has and leaves them to work out what that costs them.
            // The standing idiot-proof ruling (Adrian, 2026-09-01, "make it idiot proof, I will be
            // driving it") puts the consequence first and the mechanism second, and ends on who does
            // the work if it goes wrong. Same fact, same honesty, read in the order a person needs it.
            return new RemediationReversibility(false,
                $"{NoRollbackMarker} the app cannot undo this one on its own. Putting it back needs "
                + "more than switching a single option on or off, so there is no one statement to run "
                + "in reverse. If the change does not verify, it stays in place and you would put it "
                + "back yourself.");
        }

        /// <summary>
        /// WHY A BATCH UNDO HAS NOTHING TO RUN for one item, chosen by what that item's op kind
        /// actually captures. The reason a batch rollback prints when the applied item carries no
        /// pre-change value.
        ///
        /// <para>⚠ IT USED TO SAY THE SAME THING FOR EVERY KIND, AND FOR MOST KINDS THAT WAS WRONG
        /// (fix round 1, gate blocker 5). BatchRemediationDriver printed "the value ... found before
        /// it changed anything was never read" for every item with a null PreChangeValue. For an
        /// sp_configure fix that is exactly right: there IS a value, and it was not read. For every
        /// other kind there is no value to read — a created index, a completed backup, a finished
        /// CHECKDB and a per-database SET capture no integer before-state at all, by design. Telling
        /// an operator a read was missed sends them looking for a fault that does not exist, and
        /// implies a retry would capture it. The accurate sentences already existed inside
        /// DbatoolsRemediationExecutor, on paths the batch undo never travels; this is the producer
        /// that makes them reachable from the batch path, so the two cannot drift.</para>
        /// </summary>
        /// <param name="template">The item's template. Null (an unregistered key) gets the honest unknown.</param>
        /// <param name="templateKey">Named in the sentence when the template itself cannot be.</param>
        public static string NoBatchUndoReason(RemediationTemplate? template, string templateKey)
        {
            const string StillThere = " This fix is still in place on the server.";
            var key = string.IsNullOrWhiteSpace(templateKey) ? "this fix" : $"'{templateKey.Trim()}'";

            if (template is null)
                return $"{key} is not a template this build carries, so the app has nothing recorded "
                     + "to put back." + StillThere;

            if (!template.Reversible)
                return $"{key} declares itself not reversible, so the app never captured anything to "
                     + "put back." + StillThere;

            var configName = template.Operation?.ConfigName;
            var shown = string.IsNullOrWhiteSpace(configName) ? key : $"'{configName}'";

            return template.Operation?.OpKind switch
            {
                // The one kind that HAS a value and could genuinely have failed to read it.
                RemediationOpKind.SpConfigure =>
                    $"the configured value of {shown} was not read before this batch changed it, so "
                    + "there is nothing to put back." + StillThere,

                // A per-database SET. Its inverse needs the offenders set as it was BEFORE the apply,
                // which is not captured — the same fact DbatoolsRemediationExecutor states on its own
                // non-toggle arm ("there is no inverse to run").
                RemediationOpKind.DbSetOption =>
                    $"{key} changes a setting on each affected database, and the app does not record "
                    + "how each one was set beforehand, so there is no inverse to run." + StillThere,

                // Everything else is a one-shot action, not a settings change: there is no earlier
                // value in existence, so nothing was missed and nothing can be re-read.
                null =>
                    $"{key} carries no structured change the app can invert, so there is nothing to "
                    + "put back." + StillThere,
                _ =>
                    $"{key} is not a settings change, so there is no earlier value to put back and a "
                    + "batch undo has nothing to run for it." + StillThere,
            };
        }

        /// <summary>
        /// Reversibility for a template with no structured undo path of its own. Used for the
        /// one-shot kinds (a completed backup, a finished CHECKDB, a deleted job) where honesty is
        /// the whole answer.
        /// </summary>
        public static RemediationReversibility ForTemplate(RemediationTemplate? template) =>
            template is not null && !template.Reversible
                ? new RemediationReversibility(false,
                    $"{NoRollbackMarker} this fix is not reversible. Once it is applied, the app cannot put it back.")
                : new RemediationReversibility(null,
                    "Reversibility for this fix depends on what the server reports at apply time.");

        /// <summary>
        /// What the operator reads AFTER an apply. Every state that carries a reason RENDERS that
        /// reason: a state name on its own tells a person nothing they can act on.
        ///
        /// <para>⚠ INCLUDING <c>Confirmed</c> (fix round, gate blocker 3). This arm used to return
        /// "rolled back, {confirmedText}" and drop <paramref name="rollbackError"/> on the floor —
        /// and Confirmed is exactly the state that carries this lane's most important new sentence.
        /// The executor writes "The configured value is back at 0. The engine is still using 16,
        /// which it was before this change as well (SQL Server coerces this setting or needs a
        /// restart)." onto a CONFIRMED rollback, and the page threw it away, so the operator read
        /// the word "confirmed" and never learned the engine was still running the other number.
        /// The field is a REASON, not an ERROR, on this state; the name is historical.</para>
        /// </summary>
        /// <param name="confirmedText">
        /// What "confirmed" means for this template, in the caller's own words (e.g. "the old value
        /// is back"). Kept as a parameter because each surface knows its own object.
        /// </param>
        public static string DescribeState(
            RemediationRollbackState state, string? rollbackError, string confirmedText)
        {
            var reason = string.IsNullOrWhiteSpace(rollbackError) ? string.Empty : " " + rollbackError!.Trim();
            return state switch
            {
                RemediationRollbackState.Confirmed =>
                    string.IsNullOrWhiteSpace(rollbackError)
                        ? $"rolled back, {confirmedText}"
                        : $"rolled back, {confirmedText}.{reason}",
                RemediationRollbackState.Unconfirmed =>
                    "a rollback ran. It could not be confirmed, so this server's state is unknown. "
                    + "Check the server before relying on this record." + reason,
                RemediationRollbackState.Failed =>
                    "a rollback ran and FAILED. The change may still be in place. Check the server."
                    + reason,
                RemediationRollbackState.NotAvailable =>
                    string.IsNullOrWhiteSpace(rollbackError)
                        ? "no rollback was attempted, so the change may still be in place. Check the server."
                        : rollbackError!.Trim(),
                _ => string.Empty,
            };
        }
    }
}
