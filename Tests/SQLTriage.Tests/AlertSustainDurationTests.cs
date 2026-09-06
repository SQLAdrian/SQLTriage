/* In the name of God, the Merciful, the Compassionate */

// ── "Sustain duration (sec)" was a control that did nothing (strings lane fix round, 2026-08-28) ─
//
// WHAT WAS WRONG. Pages\Alerts.razor offered a number input bound to
// _editingAlert.DurationSeconds under the caption:
//
//     "Must stay over threshold for this long before firing. 0 = immediate."
//
// Nothing read the property. A whole-tree census of DurationSeconds across every .cs and .razor
// outside bin/obj found: the declaration on AlertDefinition, that one editor binding, two Portal
// test fixtures, and a set of unrelated types that happen to share the name (AgentJobExecution,
// BlockingEvent, ScheduledTaskModels, PerformanceReport, TopWaitDurationSeconds). There was no
// reader in AlertEvaluationService or anywhere else. An operator who set 60 to damp a flapping
// alert got a persisted number and an alert that still fired on the very next breach.
//
// The same commit that added two honesty notices to the Unit and Warning Threshold controls beside
// it left this one making a false claim about behaviour, which is why it is here rather than in the
// original clusters.
//
// WHY REMOVED AND NOT IMPLEMENTED. Two shipped alerts carry durationSeconds 30 -
// instance_unreachable and machine_unreachable, both Critical - so honouring the value would delay
// the two alerts that say a server is down by thirty seconds, on every install, as a side effect of
// making a control real. That is a product decision, not a tidy-up. The damping need the caption
// claimed to serve is already met by Cooldown / Next Alert Delay, which IS read. The property stays
// on the model so an installed config round-trips its value instead of losing it on the next Save.
//
// THIS FILE IS A TRIPWIRE IN BOTH DIRECTIONS. If the control comes back, the first test fails. If
// someone implements the behaviour in the evaluator, the second fails and tells them the control
// may now return - and that they have to rule on the two Critical alerts first.

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests
{
    public class AlertSustainDurationTests
    {
        private static string RepoRoot() => RawPassedScan.RepoRoot().FullName;

        /// <summary>A razor comment block, which the renderer strips and the operator never sees.
        /// Stripping it first is not tidiness: without it this census reads the note explaining the
        /// removal as the control itself, which is how a lint goes red on its own author.</summary>
        private static readonly Regex RazorCommentBlock =
            new(@"@\*.*?\*@", RegexOptions.Compiled | RegexOptions.Singleline);

        [Fact]
        public void The_alert_editor_offers_no_control_bound_to_the_unread_sustain_duration()
        {
            var razor = File.ReadAllText(Path.Combine(RepoRoot(), "Pages", "Alerts.razor"));
            var markup = RazorCommentBlock.Replace(razor, "");

            Assert.DoesNotContain("DurationSeconds", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Sustain duration", markup, StringComparison.Ordinal);

            // Non-vacuity: the file really is the alert editor, and the controls that DO work are
            // still there. A census over the wrong file would pass this test trivially.
            Assert.Contains("Warning Threshold", markup, StringComparison.Ordinal);
            Assert.Contains("Cooldown / Next Alert Delay", markup, StringComparison.Ordinal);
        }

        /// <summary>
        /// The claim the removal rests on, re-measured rather than remembered: the evaluator does not
        /// consult the property. This is the file that would have to change to implement it, so it is
        /// the file this test reads.
        /// </summary>
        [Fact]
        public void The_evaluator_still_does_not_read_the_sustain_duration()
        {
            var evaluator = File.ReadAllText(
                Path.Combine(RepoRoot(), "Data", "Services", "AlertEvaluationService.cs"));

            Assert.False(evaluator.Contains("DurationSeconds", StringComparison.Ordinal),
                "AlertEvaluationService now mentions DurationSeconds. If sustain-duration has been "
                + "implemented, the editor control may come back - but rule on this first: "
                + "instance_unreachable and machine_unreachable both ship durationSeconds 30 and both "
                + "are Critical, so honouring the value delays the two alerts that say a server is "
                + "down by thirty seconds on every install. Set them to 0 or say in their "
                + "descriptions that they wait. Then delete this test in the same commit.");
        }

        /// <summary>The property survives so an installed config round-trips it, and it says out loud
        /// that nothing reads it. A bare int with a default of 300 and no note is how this control
        /// gets rebuilt by someone who assumes the field means something.</summary>
        [Fact]
        public void The_model_property_says_that_nothing_reads_it()
        {
            var model = File.ReadAllText(
                Path.Combine(RepoRoot(), "Data", "Models", "AlertConfiguration.cs"));

            Assert.Contains("public int DurationSeconds", model, StringComparison.Ordinal);
            Assert.Contains("NOT READ BY ANYTHING", model, StringComparison.Ordinal);
        }
    }
}
