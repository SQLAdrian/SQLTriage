/* In the name of God, the Merciful, the Compassionate */

// -- special-alerts-escalation-parity (2026-09-11): the fix, on the harness that proved the bug ---
//
// THIS CLASS WAS WRITTEN TO PROVE A DEFECT AND NOW PINS ITS FIX. Its previous incarnation
// (special-alerts-cannot-escalate, 2026-09-10) established that AlertEvaluationService had two
// firing paths and ONE escalation block: the standard path, ApplyObservedValueAsync, carried it
// inline, and the special/queryMode path, EvaluateSpecialAlertAsync, had no escalation of any kind
// while seven shipped alerts - four of them Critical - routed through it.
//
// THE INVARIANT THIS CLASS NOW DEFENDS. **Every evaluation path that can fire an alert must
// evaluate escalation for that alert, on every firing cycle.** The escalation block is no longer
// inline anywhere: both paths call ApplyEscalationForFiringCycle, the one shared callee, from the
// same position - outside the new-vs-re-fire branch, inside the "it is breaching" arm. Three tests
// below were INVERTED to assert the parity instead of the gap; the controls around them were left
// exactly as they were, because a control that changes with the subject is not a control.
//
// THE SECOND INVARIANT: ONE ESCALATION PER EPISODE. An episode ends when the alert is MEASURED and
// found not to be breaching - never when ResolveCleared reaps it for want of measurement. That
// distinction is not decorative: the reap-and-rebuild route is REPRODUCED below on the real reaper,
// with a control proving the same harness can show a second escalation when the episode really
// does end.
//
// WHY THIS CLASS IS SHAPED THE WAY IT IS. An absence is evidence ONLY once the instrument is proved
// able to display a positive (memory: a-control-that-cannot-reproduce-cannot-refute). Two of this
// project's lanes have already reported "no signal" from an instrument that could not have shown
// one. So the headline test drives BOTH paths through ONE engine and ONE logger, with IDENTICAL
// escalation settings, and asserts the standard alert escalated in the SAME captured log that the
// special alert's silence is read from. If the harness cannot escalate anything, the control goes
// red first and the negative arm's silence is never reached.
//
// THE TRAP THAT WOULD HAVE MADE THIS VACUOUS. The escalation block is gated
// `alert.Escalate && !_dryRun` (AlertEvaluationService.cs:2385, inside the shared callee
// ApplyEscalationForFiringCycle at :2368-2426). EVERY pre-existing special-alert test in this suite runs
// with DryRun = true, under which NEITHER path can escalate. Copying one verbatim yields a silent
// absence on both arms and proves nothing. These tests set DryRun = false deliberately, and
// Preflight_noOutboundChannelIsEnabled below is what makes that safe.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;

// SQLTriage.Data declares a LogLevel of its own, so the bare name is ambiguous here. The one that
// matters is the logging framework's - the level the shipped host actually filters on.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SQLTriage.Tests
{
    public class SpecialAlertEscalationTests : IDisposable
    {
        private readonly string _tempDir;

        public SpecialAlertEscalationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "special-escalation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        // -- 0. PREFLIGHT: running with DryRun = false must not page anybody ----------

        /// <summary>
        /// DryRun = false turns the real dispatcher on. DispatchEscalation (:2490) hands its
        /// notification to NotificationChannelService.DispatchAsync, which fans out to
        /// Teams/Slack/Webhook/PagerDuty/ServiceNow/WhatsApp on each channel's own Enabled flag,
        /// loaded from <c>AppContext.BaseDirectory/Config/notification-channels.json</c>. No such
        /// file exists in this tree, so defaults apply and the fan-out selects nothing.
        ///
        /// <para>This is asserted rather than assumed: if that file ever appears in the test output
        /// with a channel enabled, this test goes red BEFORE the escalation tests below can send a
        /// real Teams card or open a real ServiceNow ticket.</para>
        /// </summary>
        [Fact]
        public async Task Preflight_noOutboundChannelIsEnabled_soAnEscalationTestCannotPageAnybody()
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);

            var critical = new AlertNotification
            {
                AlertName = "[ESCALATED] preflight",
                Metric = "preflight",
                Severity = "critical",
                InstanceName = "preflight",
                Message = "preflight",
                TriggeredAt = DateTime.UtcNow,
            };

            var results = await channels.DispatchAsync(critical);
            Assert.Empty(results);
        }

        // -- 1. THE HEADLINE: control and subject, one engine, one log ----------------

        /// <summary>
        /// THE LANE'S PROOF. Two alerts, identical escalation settings, driven through one engine
        /// with one captured log:
        ///
        /// <list type="number">
        /// <item>ARM A (POSITIVE CONTROL, standard path) - escalates. "Alert escalated:" is in the
        ///   log, EscalatedAt is stamped, and the runtime severity is lifted Warning to Critical.</item>
        /// <item>ARM B (SUBJECT, special path) - fires AND escalates, over two cycles, so the
        ///   re-fire branch is covered too.</item>
        /// </list>
        ///
        /// <para>Arm A is asserted FIRST and in the SAME log Arm B is read from. That ordering is
        /// the whole point: a harness that cannot escalate anything fails here, at the control, and
        /// never gets to make a claim about the special path.</para>
        ///
        /// <para><b>INVERTED 2026-09-11 (special-alerts-escalation-parity).</b> Arm B used to assert
        /// the absence - <c>Assert.False(specialState.IsEscalated)</c>, no escalation line, exactly
        /// one escalation line in the whole log. It now asserts the presence, and TWO escalation
        /// lines. Nothing else in the test moved: same engine, same log, same settings, same two
        /// cycles. That is the smallest possible edit that turns the proof of the defect into the
        /// pin on its fix, which is why the assertions were inverted rather than the test replaced.</para>
        /// </summary>
        [Fact]
        public async Task StandardPathEscalates_andSoNowDoesTheSpecialPath_sameEngineSameLogSameSettings()
        {
            var log = new LevelCapturingLogger();
            using var svc = BuildEngine(log);
            svc.DryRun = false;   // the escalation block is gated on !_dryRun (:2385)

            var suffix = Guid.NewGuid().ToString("N");
            var standardServer = "std-" + suffix;
            var standard = StandardEscalating(suffix);
            var special = SpecialEscalating(suffix);
            var (connection, deadEndpoint) = DeadEndpoint();

            // -- ARM A: the positive control -----------------------------------------
            // Non-cumulative, so ObserveAndApplyAsync takes the else branch at :1729 and hands the
            // raw 1 straight to ApplyObservedValueAsync. warning = 0 and greater_than is strict, so
            // 1 fires. EscalationThresholdEvents = 0 routes the decision to the TIME branch (:2404),
            // where (UtcNow - FirstTriggered).TotalMinutes >= 0 is true on the first cycle.
            await svc.ObserveAndApplyAsync(standard, standardServer, raw: 1, nowUtc: DateTime.UtcNow);

            var standardState = Assert.Single(
                svc.ActiveAlerts.Where(s => s.AlertId == standard.Id));

            // THE INSTRUMENT IS PROVED HERE. Everything after this line depends on it.
            Assert.True(standardState.IsEscalated,
                "POSITIVE CONTROL FAILED: the standard path did not escalate, so this harness cannot " +
                "display an escalation and the special path's silence below would prove nothing.");
            Assert.Equal("Critical", standardState.Severity);   // lifted from Warning at :1915
            Assert.Contains(log.At(LogLevel.Warning),
                t => IsEscalationLine(t) && t.Contains(standard.Name, StringComparison.Ordinal));

            // -- ARM B: the subject --------------------------------------------------
            // Same engine, same log, same escalation settings. connectivity_check against a refused
            // endpoint measures exactly 1, which clears the same warning = 0 threshold.
            await svc.EvaluateSpecialAlertAsync(special, connection, deadEndpoint, new AlertGlobalDefaults());

            var specialState = Assert.Single(
                svc.ActiveAlerts.Where(s => s.AlertId == special.Id));

            // NON-VACUITY: the special alert really did fire. Without this, "it did not escalate"
            // could just mean "nothing happened at all".
            Assert.Contains(log.At(LogLevel.Warning),
                t => IsFireLine(t) && t.Contains(special.Name, StringComparison.Ordinal));

            // Second cycle: exercises the re-fire branch (:1225-1245), the branch a time-based
            // escalation would have to be decided in on any real deployment.
            await svc.EvaluateSpecialAlertAsync(special, connection, deadEndpoint, new AlertGlobalDefaults());
            specialState = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == special.Id));
            Assert.True(specialState.HitCount >= 2, "the second cycle did not re-fire the special alert");

            // THE FIX (this was Assert.False + Assert.DoesNotContain until 2026-09-11).
            Assert.True(specialState.IsEscalated,
                "the special path did not escalate: ApplyEscalationForFiringCycle is either not " +
                "called from EvaluateSpecialAlertAsync or is called from the wrong position.");
            Assert.Contains(log.At(LogLevel.Warning),
                t => IsEscalationLine(t) && t.Contains(special.Name, StringComparison.Ordinal));

            // And the control is still standing in this same log at the end of the run.
            Assert.Contains(log.At(LogLevel.Warning),
                t => IsEscalationLine(t) && t.Contains(standard.Name, StringComparison.Ordinal));

            // CARDINALITY. Two alerts, two escalations - and the special alert cycled TWICE, so a
            // per-cycle escalation would read 3 here, not 2.
            Assert.Equal(2, log.At(LogLevel.Warning).Count(IsEscalationLine));
        }

        // -- 1b. THE TIGHTEST CONTROL: ONE definition, both routes -------------------

        /// <summary>
        /// The headline test above compares two DIFFERENT alert definitions, so a sceptic could
        /// say the difference lay in the definitions rather than in the code path. This one removes
        /// that objection entirely: ONE AlertDefinition object, with a queryMode set, is driven
        /// through BOTH methods on the same engine. Both routes must now escalate it, because both
        /// routes call the same escalation callee.
        ///
        /// <para><b>INVERTED 2026-09-11.</b> The special route's arm read
        /// <c>Assert.False(viaSpecial.IsEscalated)</c> with exactly ONE escalation line in the log;
        /// it now reads True with TWO. This is the tightest statement of the lane's invariant that
        /// the suite contains: one definition, two paths, one outcome.</para>
        /// </summary>
        [Fact]
        public async Task OneDefinitionBothRoutes_escalatesViaBothPaths_becauseBothCallOneCallee()
        {
            var log = new LevelCapturingLogger();
            using var svc = BuildEngine(log);
            svc.DryRun = false;

            var suffix = Guid.NewGuid().ToString("N");
            var alert = SpecialEscalating(suffix);          // ONE object, used for both routes
            var standardServer = "via-standard-" + suffix;
            var (connection, deadEndpoint) = DeadEndpoint();

            // Route 1: the standard path. ApplyObservedValueAsync ignores QueryMode entirely, so
            // this is the very same definition evaluated by the method that owns the escalation
            // block. Distinct server name => distinct stateKey, so the two routes cannot collide.
            await svc.ObserveAndApplyAsync(alert, standardServer, raw: 1, nowUtc: DateTime.UtcNow);

            // Route 2: the special path, against the refused endpoint.
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());

            var viaStandard = Assert.Single(svc.ActiveAlerts.Where(s => s.ServerName == standardServer));
            var viaSpecial  = Assert.Single(svc.ActiveAlerts.Where(s => s.ServerName == deadEndpoint));

            // Both really fired: neither claim below is vacuous.
            Assert.Equal(alert.Id, viaStandard.AlertId);
            Assert.Equal(alert.Id, viaSpecial.AlertId);
            Assert.True(viaSpecial.HitCount >= 2, "the special route did not re-fire");

            // THE POSITIVE, on the same object the negative is read from.
            Assert.True(viaStandard.IsEscalated,
                "POSITIVE CONTROL FAILED: this exact definition did not escalate even on the path " +
                "that owns the escalation block, so the special route's silence proves nothing.");

            // THE FIX: same object, same flags, other method, SAME outcome.
            Assert.True(viaSpecial.IsEscalated,
                "one definition escalated on the standard route and not on the special one, which " +
                "is the exact defect this lane closed.");

            // And in the shared log: one escalation line per route, and the special route ran TWO
            // cycles, so 2 (not 3) is also the cardinality pin.
            Assert.Equal(2, log.At(LogLevel.Warning).Count(IsEscalationLine));
            Assert.Equal(2, log.At(LogLevel.Warning).Count(IsFireLine));   // one per route
        }

        // -- 2. SECOND CONTROL: the event-count branch -------------------------------

        /// <summary>
        /// The control above takes the TIME branch with EscalationAfterMinutes = 0, which a reader
        /// could call degenerate. This one takes the OTHER branch (:2404-2411): one enqueued hit
        /// satisfies <c>recentHits &gt;= EscalationThresholdEvents</c> with a threshold of 1 inside
        /// a five-minute window, while the time branch is set beyond reach. Same conclusion,
        /// different code path, no sleeping.
        /// </summary>
        [Fact]
        public async Task PositiveControl_standardPath_eventCountBranch_alsoEscalates()
        {
            var log = new LevelCapturingLogger();
            using var svc = BuildEngine(log);
            svc.DryRun = false;

            var suffix = Guid.NewGuid().ToString("N");
            var alert = StandardEscalating(suffix);
            alert.EscalationAfterMinutes = 999999;      // the time branch could never fire
            alert.EscalationThresholdEvents = 1;        // so the decision must come from the count
            alert.EscalationWindowMinutes = 5;

            await svc.ObserveAndApplyAsync(alert, "std-" + suffix, raw: 1, nowUtc: DateTime.UtcNow);

            var state = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.True(state.IsEscalated);
            Assert.Contains(log.At(LogLevel.Warning),
                t => IsEscalationLine(t) && t.Contains(alert.Name, StringComparison.Ordinal));
        }

        // -- 3. NEGATIVE CONTROL ON THE CONTROL: the flag is what does it ------------

        /// <summary>
        /// Proves the escalation in Arm A is caused by <c>Escalate = true</c> and not by anything
        /// incidental to the harness: the identical standard-path alert with the flag cleared fires
        /// and stays un-escalated at Warning. Without this, "the special path lacks the flag's
        /// effect" and "the special path lacks the block" are not distinguishable.
        /// </summary>
        [Fact]
        public async Task NegativeControl_standardPathWithEscalateFalse_firesButDoesNotEscalate()
        {
            var log = new LevelCapturingLogger();
            using var svc = BuildEngine(log);
            svc.DryRun = false;

            var suffix = Guid.NewGuid().ToString("N");
            var alert = StandardEscalating(suffix);
            alert.Escalate = false;

            await svc.ObserveAndApplyAsync(alert, "std-" + suffix, raw: 1, nowUtc: DateTime.UtcNow);

            var state = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.Contains(log.At(LogLevel.Warning),
                t => IsFireLine(t) && t.Contains(alert.Name, StringComparison.Ordinal));
            Assert.False(state.IsEscalated);
            Assert.Equal("Warning", state.Severity);
            Assert.Empty(log.At(LogLevel.Warning).Where(IsEscalationLine));
        }

        // -- 4. THE SHIPPED SPECIAL ALERT, not a synthetic one -----------------------

        /// <summary>
        /// Arm B with the alert an operator actually edits: <c>instance_unreachable</c> read off
        /// Config/alert-definitions.json, with Escalate ticked and a time-based escalation set -
        /// exactly what Pages/Alerts.razor:841-880 offers for it, ungated by QueryMode. The engine
        /// used to ignore all three settings; it now honours them.
        ///
        /// <para><b>INVERTED 2026-09-11.</b> The last two assertions were
        /// <c>Assert.False(state.IsEscalated)</c> and <c>Assert.Empty(... IsEscalationLine)</c>.
        /// This is the operator-facing statement of the fix: the tickbox on the Alerts page is no
        /// longer inert for the seven queryMode alerts.</para>
        /// </summary>
        [Fact]
        public async Task ShippedInstanceUnreachable_withEscalationTickedOn_nowEscalates()
        {
            var log = new LevelCapturingLogger();
            using var svc = BuildEngine(log);
            svc.DryRun = false;

            var alert = Shipped("instance_unreachable");
            Assert.Equal("connectivity_check", alert.QueryMode);
            Assert.False(alert.Escalate);   // as seeded: the defect is LATENT until an operator ticks it

            // What the operator does in the UI.
            alert.Id = alert.Id + "-" + Guid.NewGuid().ToString("N");   // history isolation only
            alert.Escalate = true;
            alert.EscalationAfterMinutes = 0;
            alert.EscalationThresholdEvents = 0;
            alert.EscalationWindowMinutes = 0;

            var (connection, deadEndpoint) = DeadEndpoint();
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());

            var state = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.True(state.HitCount >= 2);
            Assert.Contains(log.At(LogLevel.Warning), IsFireLine);      // it fired, twice-cycled
            Assert.True(state.IsEscalated,                              // and now escalates
                "the escalation tickbox is still inert for a shipped queryMode alert.");

            // ONE escalation across TWO firing cycles: the tickbox pages the operator once, not once
            // per evaluation.
            Assert.Single(log.At(LogLevel.Warning).Where(IsEscalationLine));
        }

        // -- 5. THE SIDE-EFFECT SURFACE Adrian is being asked to rule on -------------

        /// <summary>
        /// Two shipped doc comments contradict each other about escalation and email:
        /// AlertEvaluationService.cs:2504 says an escalation "never reaches the SMTP channel";
        /// Data/Models/AlertConfiguration.cs:168 (<c>SendEmail</c>) said "Escalation notifications
        /// always send email regardless". This settles it against the built object rather than
        /// repeating either.
        ///
        /// <para>The doc comment lost that argument and now records the correction; this test's
        /// NAME keeps :157, the line the false sentence occupied when it was measured, which is
        /// why the name is not renamed with the line number. Citations re-derived 2026-09-11 by
        /// the fix round.</para>
        /// </summary>
        [Fact]
        public void EscalationNotification_isCriticalAndNeverEmail_soAlertConfigurationLine157IsWrong()
        {
            var alert = StandardEscalating("doc");
            var state = new AlertState
            {
                AlertId = alert.Id,
                AlertName = alert.Name,
                ServerName = "srv",
                Severity = "Critical",
                LastValue = 1,
                ThresholdValue = 0,
            };

            var n = AlertEvaluationService.BuildEscalationNotification(alert, state, "msg");

            Assert.Equal("critical", n.Severity);
            Assert.False(n.SendEmail);   // :2504 is correct; AlertConfiguration.cs:168 carried the lie
        }

        // -- 6. PLACEMENT: the decision is taken on EVERY firing cycle, not the first ------

        /// <summary>
        /// THE PIN THAT PLACEMENT IS RIGHT, and it is a different claim from "the call exists".
        ///
        /// <para>A time-based escalation is FALSE by construction on the cycle that creates the
        /// state - FirstTriggered is <c>UtcNow</c> at that instant - so it can only ever become true
        /// on a LATER cycle. Put the escalation call inside the first-fire arm and it compiles,
        /// reads correctly, satisfies any census that only asks "is it called", and escalates
        /// nothing, ever. This test refuses that shape: the escalation condition is UNMET on cycle 1
        /// and MET on cycle 2, so the call must live outside the new-vs-re-fire branch to pass.</para>
        ///
        /// <para>The event-count branch is used rather than the clock so the test needs no sleep:
        /// two hits are required, cycle 1 enqueues one, cycle 2 enqueues the second. Two further
        /// cycles then run to pin the cardinality - one page per episode, not one per cycle.</para>
        /// </summary>
        [Fact]
        public async Task SpecialPath_escalatesOnALaterCycle_soTheCallCannotLiveInTheFirstFireArm()
        {
            var log = new LevelCapturingLogger();
            using var svc = BuildEngine(log);
            svc.DryRun = false;

            var alert = SpecialEscalating(Guid.NewGuid().ToString("N"));
            alert.EscalationAfterMinutes = 999999;   // the time branch can never fire
            alert.EscalationThresholdEvents = 2;     // so TWO hits are what decides it
            alert.EscalationWindowMinutes = 5;

            var (connection, deadEndpoint) = DeadEndpoint();
            var defaults = new AlertGlobalDefaults();

            // Cycle 1 - the FIRST-FIRE arm. One hit; the threshold is two.
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, defaults);
            var afterOne = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.Equal(1, afterOne.HitCount);
            Assert.False(afterOne.IsEscalated);
            Assert.Empty(log.At(LogLevel.Warning).Where(IsEscalationLine));

            // Cycle 2 - the RE-FIRE arm. The second hit meets the threshold. If the escalation call
            // sat inside the first-fire arm, nothing would evaluate it here and this would be red.
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, defaults);
            var afterTwo = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.Equal(2, afterTwo.HitCount);
            Assert.True(afterTwo.IsEscalated,
                "the escalation was not evaluated on the re-fire cycle. Either the special path " +
                "does not call ApplyEscalationForFiringCycle, or the call is inside the new-alert " +
                "arm, where a time- or count-based escalation can never become true.");
            Assert.Single(log.At(LogLevel.Warning).Where(IsEscalationLine));

            // CARDINALITY: two more firing cycles, still exactly one page.
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, defaults);
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, defaults);
            Assert.Single(log.At(LogLevel.Warning).Where(IsEscalationLine));
        }

        // -- 7. THE MEASUREMENT-GAP ROUTE: reproduced first, then guarded ------------------

        /// <summary>
        /// ADRIAN'S RULING, 2026-09-10 21:15: <c>windows_power_plan</c> escalates ONCE PER EPISODE,
        /// never again after a measurement gap. The route the guard defends against was BELIEVED,
        /// not run, when this lane opened. This test runs it, step by step, on the real components:
        ///
        /// <list type="number">
        /// <item>The alert breaches and pages once.</item>
        /// <item>The alert then stops firing while its measurement clock stays fresh, so it reads
        ///   "measured recently, last fired hours ago" - which is what ShouldAutoResolveAsCleared
        ///   treats as cleared. The predicate is asserted here on the power plan's OWN frequency,
        ///   read from the shipped catalogue, not from memory.
        ///
        ///   <para>⚠ THE MECHANISM BEHIND THIS STEP CHANGED ON 2026-09-15 (alert-stamp-key-per-
        ///   server) AND THE OLD DESCRIPTION IS NOW FALSE. It used to read: <c>_lastEvaluation</c>
        ///   is stamped BEFORE evaluation and unconditionally by RecordEvaluationAttempt, so a cycle
        ///   whose measurement FAILED still presented as "evaluated seconds ago". That was the
        ///   defect, not the design, and it is severed: a failed, timed-out or breaker-skipped
        ///   attempt now stamps NOTHING, and ResolveCleared reads <c>_lastServerEvaluation</c> -
        ///   written only by RecordServerEvaluation, only when the server answered (on this special
        ///   path, a reading in hand; on the standard path, since the owner's ruling of 2026-09-17, a
        ///   query that completed, NULL included), and keyed per
        ///   (alert, server). What reaches the reap below is therefore step 1's SUCCESSFUL
        ///   measurement stamp plus the backdated LastTriggered, which is the honest clear, not the
        ///   measurement-gap route. <b>So this test no longer reproduces a gap-induced reap; it
        ///   pins what happens AFTER any reap.</b> That is still the behaviour it guards - see the
        ///   next paragraph - but a reader looking here for the gap defect will not find it, and
        ///   the H1/H2 tests in AlertStampKeyPerServerTests are where that route now lives.</para></item>
        ///   <item>ResolveCleared - the REAL reaper, not a re-implementation - removes a state that
        ///   is still breaching.</item>
        /// <item>The next successful measurement rebuilds it. The rebuild is proved genuine by its
        ///   HitCount returning to 1, which is what makes the second page possible at all: a fresh
        ///   AlertState has EscalatedAt null.</item>
        /// </list>
        ///
        /// <para><b>The guard, and how to know it is doing the work.</b> One page, not two, and the
        /// rebuilt state carries the ORIGINAL escalation instant. The companion test
        /// <see cref="AMeasuredResolveEndsTheEpisode_soTheNextOutbreakEscalatesAgain"/> drives the
        /// same engine to a SECOND page by ending the episode the legitimate way, so the "still one"
        /// below is a measurement and not an instrument that cannot count past one.</para>
        ///
        /// <para><b>Why connectivity_check stands in for registry_check.</b> The real
        /// <c>windows_power_plan</c> handler runs <c>xp_instance_regread</c> and needs a live SQL
        /// Server. Everything the ROUTE turns on is the power plan's own and is asserted from the
        /// shipped definition first - the 3600 s frequency that sets the 3-hour reap window, its
        /// Critical severity, its greater-than-1 threshold. Only the measurement is substituted, for
        /// one that returns the same breaching value against a refused socket.</para>
        /// </summary>
        [Fact]
        public async Task EscalationSurvivesAMeasurementGapReap_oneEpisodeOnePage()
        {
            // Measured from the shipped catalogue, not remembered.
            var powerPlan = Shipped("windows_power_plan");
            Assert.Equal("registry_check", powerPlan.QueryMode);
            Assert.Equal(3600, powerPlan.FrequencySeconds);
            Assert.Equal("Critical", powerPlan.Severity);
            Assert.Equal(1, powerPlan.Thresholds.Warning);
            Assert.False(powerPlan.Escalate);        // latent as shipped; the operator ticks it

            var alert = powerPlan;
            alert.Id = "windows_power_plan_" + Guid.NewGuid().ToString("N");  // history isolation
            alert.QueryMode = "connectivity_check";  // the substituted measurement, see the prose
            // The threshold travels WITH the measurement it describes. The shipped power plan fires
            // on "greater than 1" against a registry-derived plan code; connectivity_check measures
            // exactly 1 against a refused socket, which does not clear a threshold of 1. Leaving the
            // shipped number here would have produced an alert that never fired and a test that
            // proved nothing - it did, on the first run of this test, which is why the note is here.
            alert.Thresholds = new AlertThresholds { Warning = 0 };
            alert.Escalate = true;
            alert.EscalationAfterMinutes = 0;
            alert.EscalationThresholdEvents = 0;
            alert.EscalationWindowMinutes = 0;

            var log = new LevelCapturingLogger();
            // ResolveCleared looks the alert up by id, so this engine gets a definitions store that
            // contains it - the real store, through the shipped file seam.
            using var svc = BuildEngine(log, DefinitionsFileContaining(alert));
            svc.DryRun = false;

            var (connection, deadEndpoint) = DeadEndpoint();
            var defaults = new AlertGlobalDefaults();

            // -- 1. it breaches, and pages once -----------------------------------
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, defaults);
            var first = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.True(first.IsEscalated, "the first cycle did not escalate, so there is no episode "
                + "to continue and the rest of this test would prove nothing.");
            var firstEscalatedAt = first.EscalatedAt;
            Assert.Single(log.At(LogLevel.Warning).Where(IsEscalationLine));

            // -- 2. four hours in which the registry read fails every cycle --------
            var now = DateTime.UtcNow;
            // DERIVED from the alert's own frequency, not hard-coded. This was `now.AddHours(-4)`, which
            // cleared ShouldAutoResolveAsCleared's window (frequency x3 = 3 h for this alert) by exactly
            // one hour. Widen that multiplier in the product and this fixture silently stops reaching the
            // route it exists to cover, while the failure message below tells the reader the PRODUCTION
            // guard is unnecessary. Deriving it keeps the fixture on the right side of the window by
            // construction, the way ReapAfterAMeasurementGap already does.
            first.LastTriggered = now.AddSeconds(-powerPlan.FrequencySeconds * 20); // no cycle refreshed it
            // ⚠ INERT with respect to the reaper since 2026-09-15, and kept only because it is what
            // the production cycle really does for a due alert. RecordEvaluationAttempt writes
            // _lastDueCheck - the SCHEDULER's clock - and ResolveCleared does not read it. Before
            // this lane it wrote the very clock the reaper judged by, which is what made an
            // unmeasured cycle look measured. ResolveCleared reads _lastServerEvaluation and nothing
            // else, so no value written here can reach the reap asserted below; believing this line
            // drives that reap would be believing the old, broken mechanism. (Whether the test still
            // passes with the line REMOVED is untested - nobody ran that - so it is kept.)
            svc.RecordEvaluationAttempt(alert.Id, now);

            Assert.True(
                AlertEvaluationService.ShouldAutoResolveAsCleared(
                    AlertStatus.Active, first.LastTriggered, now, powerPlan.FrequencySeconds, now),
                // ⚠ DO NOT restore the old ending, "...so the route this guard exists for does not "
                // reproduce and THE GUARD IS UNNECESSARY." That conclusion is false for the finding this
                // assertion can actually produce. The predicate failing here means THIS FIXTURE no longer
                // reaches the route — most plausibly because ShouldAutoResolveAsCleared's staleness window
                // moved — not that the production route is gone. A reader who obeyed it would delete the
                // guard in ResolveCleared whose absence "let one unbroken outage page an operator once per
                // measurement gap", per that method's own doc comment.
                "the reaper's own predicate says THIS FIXTURE'S state is not stale-and-still-measuring, so "
                + "this test no longer reaches the route it covers and proves nothing. Check "
                + "ShouldAutoResolveAsCleared's staleness window against the gap set just above — do NOT "
                + "conclude the production guard is unneeded.");

            // -- 3. the REAL reaper takes a still-breaching alert ------------------
            svc.ResolveCleared();
            Assert.Empty(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));

            // -- 4. the next successful measurement rebuilds it --------------------
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, defaults);
            var rebuilt = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));

            // It is genuinely a NEW state - HitCount back to 1, not 2 - so its EscalatedAt came from
            // the constructor as null. That is the second page, sitting there waiting to be sent.
            Assert.Equal(1, rebuilt.HitCount);

            // THE GUARD. Still one page, and the state tells the truth about when it was sent.
            Assert.Single(log.At(LogLevel.Warning).Where(IsEscalationLine));
            Assert.True(rebuilt.IsEscalated,
                "the rebuilt state does not know it already escalated, so the next cycle will page "
                + "again for the same unbroken episode.");
            Assert.Equal(firstEscalatedAt, rebuilt.EscalatedAt);
            Assert.Equal("Critical", rebuilt.Severity);
        }

        /// <summary>
        /// THE CONTROL FOR THE TEST ABOVE, and the definition of an episode. An episode ends when
        /// the alert is MEASURED and found not to be breaching - and then, and only then, the next
        /// outbreak may page again.
        ///
        /// <para>Without this test, "still exactly one escalation line" in the reap test could mean
        /// "this harness cannot produce a second escalation line". Here the same engine produces
        /// two, differing from the reap test in exactly one act: the alert was measured and had
        /// stopped breaching, rather than reaped for want of measurement.</para>
        ///
        /// <para><b>ARM 2 ADDED BY THE FIX ROUND (2026-09-11), because arm 1 alone was BLIND.</b>
        /// Arm 1 measures-clear while the AlertState is STILL ACTIVE - the one ordering in which the
        /// pre-fix code happened to work, because <c>EndEscalationEpisode</c> sat inside the
        /// <c>_activeStates.TryRemove</c> arm and a live state made that TryRemove succeed. It never
        /// ordered a REAP BEFORE the clear, which is the ordering <c>ResolveCleared</c> exists to
        /// create and which this lane's own design makes routine. In that ordering the pre-fix code
        /// ended nothing and silenced the alert for the life of the process. A control that cannot
        /// reach the failing ordering cannot refute anything (memory:
        /// a-control-that-cannot-reproduce-cannot-refute), so BOTH orderings are driven here.</para>
        ///
        /// <para>Arm 2 gets its own engine and its own captured log so the two arms' escalation-line
        /// counts cannot borrow each other's evidence.</para>
        /// </summary>
        [Fact]
        public async Task AMeasuredResolveEndsTheEpisode_soTheNextOutbreakEscalatesAgain()
        {
            // -- ARM 1: measured-clear while the state is STILL ACTIVE --------------------
            var log = new LevelCapturingLogger();
            using var svc = BuildEngine(log);
            svc.DryRun = false;

            var suffix = Guid.NewGuid().ToString("N");
            var alert = StandardEscalating(suffix);
            var server = "std-" + suffix;

            // Outbreak 1: breaching, pages.
            await svc.ObserveAndApplyAsync(alert, server, raw: 1, nowUtc: DateTime.UtcNow);
            Assert.True(Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id)).IsEscalated);
            Assert.Single(log.At(LogLevel.Warning).Where(IsEscalationLine));

            // MEASURED and no longer breaching: the episode is genuinely over.
            await svc.ObserveAndApplyAsync(alert, server, raw: 0, nowUtc: DateTime.UtcNow);
            Assert.Empty(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));

            // Outbreak 2: a new episode, and it pages.
            await svc.ObserveAndApplyAsync(alert, server, raw: 1, nowUtc: DateTime.UtcNow);
            Assert.True(Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id)).IsEscalated);
            Assert.Equal(2, log.At(LogLevel.Warning).Count(IsEscalationLine));

            // -- ARM 2: the REAP comes BEFORE the measured clear --------------------------
            // Same claim, same engine shape, one act reordered. This arm is RED against the
            // pre-fix code while arm 1 is green against it; that difference is the whole reason
            // arm 2 exists.
            var log2 = new LevelCapturingLogger();
            var suffix2 = Guid.NewGuid().ToString("N");
            var alert2 = StandardEscalating(suffix2);
            var server2 = "std-reaped-" + suffix2;
            using var svc2 = BuildEngine(log2, DefinitionsFileContaining(alert2));
            svc2.DryRun = false;

            var now2 = DateTime.UtcNow;

            // Outbreak 1: breaching, pages.
            await svc2.ObserveAndApplyAsync(alert2, server2, raw: 1, nowUtc: now2);
            var firstState = Assert.Single(svc2.ActiveAlerts.Where(s => s.AlertId == alert2.Id));
            Assert.True(firstState.IsEscalated);
            Assert.Single(log2.At(LogLevel.Warning).Where(IsEscalationLine));

            // A measurement gap, then the REAL reaper takes the still-breaching state. The reaper
            // deliberately does not end the episode - that is Adrian's ruling and it stands.
            ReapAfterAMeasurementGap(svc2, alert2, firstState, now2);
            Assert.Empty(svc2.ActiveAlerts.Where(s => s.AlertId == alert2.Id));

            // NOW the alert is MEASURED and genuinely clear. There is no state left to remove, so
            // the pre-fix code reached no EndEscalationEpisode at all and the episode never ended.
            await svc2.ObserveAndApplyAsync(alert2, server2, raw: 0, nowUtc: DateTime.UtcNow);
            Assert.Empty(svc2.ActiveAlerts.Where(s => s.AlertId == alert2.Id));

            // Outbreak 2, hours later in production terms: a NEW episode, and it must page.
            await svc2.ObserveAndApplyAsync(alert2, server2, raw: 1, nowUtc: DateTime.UtcNow);
            Assert.True(
                Assert.Single(svc2.ActiveAlerts.Where(s => s.AlertId == alert2.Id)).IsEscalated,
                "a reap followed by a measured clear did not end the episode, so this outbreak was "
                + "silenced by the once-per-episode guard.");
            Assert.Equal(2, log2.At(LogLevel.Warning).Count(IsEscalationLine));
        }

        // -- 8. THE FIX ROUND'S PINS: reap -> measured clear -> a NEW outbreak must page ---

        /// <summary>
        /// THE DEFECT THE COLD GATE PROVED, PINNED ON THE STANDARD PATH. Measured before the fix:
        /// <c>Expected: 2, Actual: 1</c> escalation lines.
        ///
        /// <para>Sequence, every step of it routine under this lane's own design:
        /// <list type="number">
        /// <item>the alert fires and escalates, so <c>_escalatedEpisodes[key]</c> is set;</item>
        /// <item>measurement stops and <c>ResolveCleared</c> reaps the still-breaching state,
        ///   deliberately NOT ending the episode (correct, and unchanged by this fix);</item>
        /// <item>measurement RESUMES and the alert is genuinely clear. With no active state left,
        ///   the pre-fix <c>EndEscalationEpisode</c> - which sat inside the <c>TryRemove</c> arm -
        ///   was never reached, and the episode entry survived for the process lifetime;</item>
        /// <item>a genuinely NEW outbreak: the guard restored the OLD <c>EscalatedAt</c>, the gate
        ///   returned, and NOTHING EVER PAGED AGAIN.</item>
        /// </list></para>
        ///
        /// <para>THE PIN IS THE SECOND PAGE. The control immediately above proves this engine can
        /// emit two escalation lines, so "one line" here is a measurement and not an instrument
        /// stuck at one.</para>
        /// </summary>
        [Fact]
        public async Task AReapThenAMeasuredClear_endsTheEpisode_standardPath_soANewOutbreakPagesAgain()
        {
            var log = new LevelCapturingLogger();
            var suffix = Guid.NewGuid().ToString("N");
            var alert = StandardEscalating(suffix);
            var server = "std-reap-" + suffix;
            using var svc = BuildEngine(log, DefinitionsFileContaining(alert));
            svc.DryRun = false;

            var now = DateTime.UtcNow;

            // 1. fires and escalates.
            await svc.ObserveAndApplyAsync(alert, server, raw: 1, nowUtc: now);
            var first = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.True(first.IsEscalated, "the first cycle did not escalate, so there is no episode "
                + "to end and the rest of this test would prove nothing.");
            var firstEscalatedAt = first.EscalatedAt;
            Assert.Single(log.At(LogLevel.Warning).Where(IsEscalationLine));

            // 2. measurement gap, then the REAL reaper.
            ReapAfterAMeasurementGap(svc, alert, first, now);
            Assert.Empty(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));

            // 3. measurement RESUMES and the alert is genuinely clear. Nothing to remove.
            await svc.ObserveAndApplyAsync(alert, server, raw: 0, nowUtc: DateTime.UtcNow);
            Assert.Empty(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));

            // 4. a NEW outbreak. It must page, and it must page as ITSELF.
            await svc.ObserveAndApplyAsync(alert, server, raw: 1, nowUtc: DateTime.UtcNow);
            var reborn = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.True(reborn.IsEscalated,
                "the new outbreak did not escalate: the episode entry from before the reap is still "
                + "in _escalatedEpisodes and the guard is silencing this alert permanently.");
            Assert.Equal(2, log.At(LogLevel.Warning).Count(IsEscalationLine));

            // ... and the state must not be reporting the PREVIOUS episode's page as this one's.
            Assert.NotEqual(firstEscalatedAt, reborn.EscalatedAt);
        }

        /// <summary>
        /// THE SAME DEFECT ON THE SPECIAL/queryMode PATH, driving the real
        /// <c>EvaluateSpecialAlertAsync</c>. The two paths carry separate copies of the
        /// not-breaching branch, so one pin does not cover the other - that separation is how this
        /// lane's original defect was born.
        ///
        /// <para><b>WHICH INSTRUMENT THIS USES, stated because the cold gate used a different one.</b>
        /// The gate proved this against a live <c>.\old2017</c> connectivity_check. This pin uses
        /// the IN-PROCESS REFUSED ENDPOINT (<c>127.0.0.1,1</c>, nothing listening) so it is
        /// deterministic and needs no SQL Server. <c>CheckConnectivityAsync</c> returns 1 =
        /// unreachable from its catch on every cycle, so the MEASUREMENT is real and identical
        /// throughout; what moves between cycles is the ALERT'S OWN THRESHOLD, which is the thing
        /// that decides breaching-vs-clear. The production branch reached is bit-for-bit the one a
        /// live server's <c>0</c> would reach: <c>IsThresholdBreached</c> false, therefore the
        /// not-breaching arm. A live-server variant would move the measurement instead of the
        /// threshold and land in the same arm.</para>
        /// </summary>
        [Fact]
        public async Task AReapThenAMeasuredClear_endsTheEpisode_specialPath_soANewOutbreakPagesAgain()
        {
            var log = new LevelCapturingLogger();
            var suffix = Guid.NewGuid().ToString("N");
            var alert = SpecialEscalating(suffix);   // connectivity_check, greater_than, Warning = 0
            using var svc = BuildEngine(log, DefinitionsFileContaining(alert));
            svc.DryRun = false;

            var (connection, deadEndpoint) = DeadEndpoint();
            var defaults = new AlertGlobalDefaults();
            var now = DateTime.UtcNow;

            // 1. the socket is refused, the alert breaches, and it escalates.
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, defaults);
            var first = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.True(first.IsEscalated, "the first cycle did not escalate, so there is no episode "
                + "to end and the rest of this test would prove nothing.");
            var firstEscalatedAt = first.EscalatedAt;
            Assert.Single(log.At(LogLevel.Warning).Where(IsEscalationLine));

            // 2. measurement gap, then the REAL reaper takes the still-breaching state.
            ReapAfterAMeasurementGap(svc, alert, first, now);
            Assert.Empty(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));

            // 3. a MEASURED, not-breaching cycle. See the prose: the measured value is still 1,
            //    the threshold it is judged against is now 5, so the not-breaching arm runs with
            //    no active state to remove - exactly the shape the pre-fix code could not end.
            alert.Thresholds = new AlertThresholds { Warning = 5 };
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, defaults);
            Assert.Empty(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));

            // 4. the outage returns: a NEW episode on the special path, and it must page.
            alert.Thresholds = new AlertThresholds { Warning = 0 };
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, defaults);
            var reborn = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.True(reborn.IsEscalated,
                "the special path's new outbreak did not escalate: the episode entry from before "
                + "the reap is still in _escalatedEpisodes and the guard is silencing it permanently.");
            Assert.Equal(2, log.At(LogLevel.Warning).Count(IsEscalationLine));
            Assert.NotEqual(firstEscalatedAt, reborn.EscalatedAt);
        }

        /// <summary>
        /// THE STATE MUST STOP LYING, not merely page again. Paging is only half the defect: before
        /// the fix the guard also stamped the NEW outbreak with the PREVIOUS episode's
        /// <c>EscalatedAt</c> and forced <c>Severity = "Critical"</c>, so the NOC read a fresh
        /// outbreak as an old, already-escalated one and every severity-gated channel was routed on
        /// a severity nothing had measured.
        ///
        /// <para>The escalation delay is lifted to 600 minutes before the new outbreak, so a correct
        /// engine has NOT escalated it yet on the cycle this test reads. That is what makes the
        /// assertions non-vacuous: post-fix the state is honestly un-escalated, Warning, with a null
        /// EscalatedAt; pre-fix the guard runs BEFORE the delay is ever consulted and stamps all
        /// three fields from the dead episode. The alert's declared severity is deliberately
        /// "Warning" (<c>StandardEscalating</c>) so a Critical can only have come from the guard - a
        /// Critical-declared alert would be lifted by RuntimeSeverity's floor and prove nothing.</para>
        /// </summary>
        [Fact]
        public async Task AfterAReapAndAMeasuredClear_theNewOutbreakDoesNotInheritTheOldEpisode()
        {
            var log = new LevelCapturingLogger();
            var suffix = Guid.NewGuid().ToString("N");
            var alert = StandardEscalating(suffix);
            var server = "std-honesty-" + suffix;
            using var svc = BuildEngine(log, DefinitionsFileContaining(alert));
            svc.DryRun = false;

            var now = DateTime.UtcNow;

            await svc.ObserveAndApplyAsync(alert, server, raw: 1, nowUtc: now);
            var first = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.True(first.IsEscalated);
            var firstEscalatedAt = first.EscalatedAt;
            Assert.Equal("Critical", first.Severity);   // the escalation itself lifted it

            ReapAfterAMeasurementGap(svc, alert, first, now);
            await svc.ObserveAndApplyAsync(alert, server, raw: 0, nowUtc: DateTime.UtcNow);
            Assert.Empty(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));

            // The operator's normal delay: this new outbreak is NOT yet due to escalate.
            alert.EscalationAfterMinutes = 600;
            await svc.ObserveAndApplyAsync(alert, server, raw: 1, nowUtc: DateTime.UtcNow);

            var reborn = Assert.Single(svc.ActiveAlerts.Where(s => s.AlertId == alert.Id));
            Assert.False(reborn.IsEscalated,
                "the new outbreak reports itself as already escalated, inherited from an episode "
                + "that ended before it began.");
            Assert.Null(reborn.EscalatedAt);
            Assert.NotEqual(firstEscalatedAt, reborn.EscalatedAt);
            Assert.Equal("Warning", reborn.Severity);

            // And nothing paged a second time, because nothing was due to.
            Assert.Single(log.At(LogLevel.Warning).Where(IsEscalationLine));
        }

        // -- helpers -----------------------------------------------------------------

        /// <summary>
        /// Puts the engine through a real measurement gap and then the REAL reaper: the state was
        /// last TRIGGERED long ago (no cycle refreshed it, because none measured a value) while an
        /// evaluation ATTEMPT was stamped just now (every cycle tried). That asymmetry is exactly
        /// what <c>ShouldAutoResolveAsCleared</c> keys on, and it is ASSERTED here rather than
        /// assumed - if the predicate stops reproducing, every test that calls this fails on this
        /// line instead of quietly proving nothing.
        /// </summary>
        private static void ReapAfterAMeasurementGap(
            AlertEvaluationService svc, AlertDefinition alert, AlertState state, DateTime nowUtc)
        {
            state.LastTriggered = nowUtc.AddSeconds(-alert.FrequencySeconds * 20);
            svc.RecordEvaluationAttempt(alert.Id, nowUtc);

            Assert.True(
                AlertEvaluationService.ShouldAutoResolveAsCleared(
                    AlertStatus.Active, state.LastTriggered, nowUtc, alert.FrequencySeconds, nowUtc),
                "the reaper's own predicate says this state is NOT stale-and-still-measuring, so the "
                + "reap-before-clear ordering does not reproduce and this test proves nothing.");

            svc.ResolveCleared();
        }

        private static bool IsFireLine(string text) =>
            text.StartsWith("Alert fired:", StringComparison.Ordinal);

        private static bool IsEscalationLine(string text) =>
            text.StartsWith("Alert escalated:", StringComparison.Ordinal);

        /// <summary>
        /// The standard-path control. Id and Name deliberately differ, so an assertion that matched
        /// the Id by accident would not pass. No Critical threshold, so RuntimeSeverity is "Warning"
        /// until the escalation block lifts it - which makes the severity assertion non-vacuous.
        /// ValueKind is left unset so IsCumulativeCounter is false and no cache or previous sample
        /// is needed (AlertConfiguration.cs:401 ValueKind, :411-412 IsCumulativeCounter).
        /// </summary>
        private static AlertDefinition StandardEscalating(string suffix) => new()
        {
            Id = "std_escalating_" + suffix,
            Name = "Standard Escalating Control " + suffix,
            Operator = "greater_than",
            Thresholds = new AlertThresholds { Warning = 0 },
            Severity = "Warning",
            Escalate = true,
            EscalationAfterMinutes = 0,      // the model DEFAULTS this to 30 - it must be set
            EscalationThresholdEvents = 0,   // 0 routes the decision to the time branch (:2404)
            EscalationWindowMinutes = 0,
        };

        /// <summary>The subject: the same escalation settings on the queryMode path, so the ONLY
        /// variable between the two arms is which method evaluates the alert.</summary>
        private static AlertDefinition SpecialEscalating(string suffix) => new()
        {
            Id = "special_escalating_" + suffix,
            Name = "Special Escalating Subject " + suffix,
            QueryMode = "connectivity_check",
            Operator = "greater_than",
            Thresholds = new AlertThresholds { Warning = 0 },
            Severity = "Critical",
            Escalate = true,
            EscalationAfterMinutes = 0,
            EscalationThresholdEvents = 0,
            EscalationWindowMinutes = 0,
        };

        /// <summary>Read off the bytes that install, anchored on the solution root - probing for
        /// Config/alert-definitions.json would find the test-output copy.</summary>
        private static AlertDefinition Shipped(string id)
        {
            var json = File.ReadAllText(Path.Combine(
                RawPassedScan.RepoRoot().FullName, "Config", "alert-definitions.json"));
            var file = System.Text.Json.JsonSerializer.Deserialize<AlertDefinitionsFile>(
                json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(file);
            return file!.Alerts.Single(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Writes a one-alert definitions store under this test's temp directory and returns its
        /// path, for the tests that need <c>_definitions.GetAlert</c> to find the alert they are
        /// driving - ResolveCleared skips any state whose definition it cannot look up, so a test
        /// of the reaper against an ad-hoc alert id would silently reap nothing. Uses the shipped
        /// internal path seam, not a mock.
        /// </summary>
        private string DefinitionsFileContaining(params AlertDefinition[] alerts)
        {
            var file = new AlertDefinitionsFile { Alerts = new List<AlertDefinition>(alerts) };
            var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + "-alert-definitions.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(file));
            return path;
        }

        private AlertEvaluationService BuildEngine(
            ILogger<AlertEvaluationService> logger, string? definitionsFilePath = null)
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);

            return new AlertEvaluationService(
                logger,
                new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, definitionsFilePath),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                channels,
                new liveQueriesCacheStore(),
                new InlineOrchestrator(),
                // Never the installed Config/eval-failures.json.
                evalFailureStorePath: Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".json"));
        }

        private static (ServerConnection, string) DeadEndpoint()
        {
            const string deadEndpoint = "127.0.0.1,1"; // nothing listens here - refused immediately
            var connection = new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = deadEndpoint,
                UseWindowsAuthentication = true,
                ConnectionTimeout = 2,
                IsEnabled = true,
            };
            return (connection, deadEndpoint);
        }

        /// <summary>Captures level AND rendered text: a line at a level production never emits is
        /// the same as no line at all.</summary>
        private sealed class LevelCapturingLogger : ILogger<AlertEvaluationService>
        {
            private readonly List<(LogLevel Level, string Text)> _lines = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => Scope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_lines) _lines.Add((logLevel, formatter(state, exception)));
            }

            public List<string> At(LogLevel level)
            {
                lock (_lines) return _lines.Where(l => l.Level == level).Select(l => l.Text).ToList();
            }

            private sealed class Scope : IDisposable
            {
                public static readonly Scope Instance = new();
                public void Dispose() { }
            }
        }

        /// <summary>Runs the work inline, exactly as the real orchestrator does on the happy path.</summary>
        private sealed class InlineOrchestrator : IQueryOrchestrator
        {
            public async Task<QueryResult> EnqueueAsync(
                QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default)
            {
                try
                {
                    await request.Work(cancellationToken);
                    return new QueryResult { QueryId = request.QueryId, Success = true };
                }
                catch (Exception ex)
                {
                    return new QueryResult { QueryId = request.QueryId, Success = false, Exception = ex };
                }
            }

            public Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorHealth());
            public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorMetrics());
            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
