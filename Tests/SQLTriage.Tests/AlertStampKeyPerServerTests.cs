/* In the name of God, the Merciful, the Compassionate */

// -- alert-stamp-key-per-server (2026-09-15): the two routes, reproduced on the real engine -------
//
// THE DEFECT, in one sentence: ShouldAutoResolveAsCleared trusted a freshness stamp that did not
// mean what it was read to mean, so alerts that were still breaching were silently resolved on
// servers nothing had measured.
//
// It had TWO INDEPENDENT HALVES, and fixing either alone leaves the other live. Both are reproduced
// below, each as its own test, each with a control in the same test proving the harness can show
// the opposite outcome.
//
//   H1 - THE KEY WAS COARSER THAN THE STATE IT GUARDED. _activeStates is per (alert, server). The
//        stamp was per (alert). So a clean run of alert X on server A refreshed the freshness used
//        to judge alert X on server B, and B was reaped as "re-checked and clear" when nothing had
//        looked at it. Live: of at least 16 silent reaps, 14 were on the one server whose circuit
//        breaker was open - every one of those intervals overlapping a breaker-OPEN window.
//
//   H2 - AN ATTEMPT COUNTED AS A MEASUREMENT. The stamp was written ABOVE the query delegate and
//        UNCONDITIONALLY, so a cycle whose query timed out stamped exactly like one that succeeded.
//        Live: the other 2 of those 16 reaps were on a server with NO breaker window at all, which
//        is what proves this half is reachable on its own and not a symptom of H1.
//
// WHY THESE TESTS SLEEP. ShouldAutoResolveAsCleared's window is FrequencySeconds x 3, and both
// halves are about a stamp being STALE while another signal is fresh. The alerts below therefore
// declare FrequencySeconds = 1 so the window is three seconds and the wait is real but short. The
// sleep is load-bearing: without it the setup measurement is still inside the window and the test
// passes for the wrong reason. If you raise the frequency, raise the wait with it.
//
// WHY THEY DRIVE THE REAL ENGINE. The pure predicate ShouldAutoResolveAsCleared was ALREADY under
// test - AlertClusterDHonestyTests covers it directly, and it was correct and stayed green
// throughout. The defect was never in the predicate. It was in the KEY of the value handed to it,
// which a test of the predicate alone cannot see. So everything below goes through the shipped
// seams: the real EvaluateSpecialAlertAsync to measure, the real RecordEvaluationAttempt the cycle
// calls for a due alert, and the real ResolveCleared to reap.
//
// FIX ROUND 2 (2026-09-17) adds five tests for the owner's two rulings, R1 "a completed query
// counts" and R2 "prune in fix round 2". They are described where they sit, below Axis3. They drive
// the STANDARD path, which H1 and H2 never reach, through the one seam that path needs:
// AlertEvaluationService.StandardQueryOverrideForTests, which yields the raw object ExecuteScalarAsync
// would return, or throws in its place, and replaces nothing else - the null and DBNull mapping after
// it runs for real. R1 (a theory, one case for DBNull and one for null) and I1 sleep for the reason
// given above. The three I2 tests do not sleep: a prune has no window, and they use a three-hour
// alert so that nothing in them is reaped for staleness.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class AlertStampKeyPerServerTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ITestOutputHelper _out;

        public AlertStampKeyPerServerTests(ITestOutputHelper output)
        {
            _out = output;
            _tempDir = Path.Combine(Path.GetTempPath(), "alert-stamp-key-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        // Two endpoints that both refuse instantly. connectivity_check MEASURES a refused socket -
        // it returns 1, it does not fail - which is what makes it a reliable offline measurement
        // here, exactly as SpecialAlertEscalationTests uses it.
        private const string ServerA = "127.0.0.1,1";
        private const string ServerB = "127.0.0.2,1";

        /// <summary>The stale window is FrequencySeconds x 3. One second gives a three second window.</summary>
        private const int FrequencySeconds = 1;
        private static readonly TimeSpan PastTheWindow = TimeSpan.FromMilliseconds(3_500);

        // =========================================================================================
        // H1 - a clean run on one server must not refresh the freshness used to judge another
        // =========================================================================================

        [Fact]
        public async Task H1_aCleanRunOnOneServerDoesNotRefreshTheFreshnessUsedToJudgeAnother()
        {
            var alert = MeasurableAlert();
            using var svc = BuildEngine(DefinitionsFileContaining(alert));
            svc.DryRun = true;
            var (connection, defaults) = Rig();

            // -- setup: one alert, measured once on each of two servers ---------------------------
            await svc.EvaluateSpecialAlertAsync(alert, connection, ServerA, defaults);
            await svc.EvaluateSpecialAlertAsync(alert, connection, ServerB, defaults);

            // HAYSTACK. Two distinct per-server states must exist before anything is read from
            // their absence. If the engine produced one state for two servers, or none at all, the
            // reap assertions below would be measuring the harness and not the fix.
            var states = svc.ActiveAlerts.Where(s => s.AlertId == alert.Id).ToList();
            Assert.True(states.Count == 2,
                $"Expected one alert state per server and got {states.Count}: "
                + string.Join(", ", states.Select(s => s.ServerName))
                + ". Nothing below can prove anything until this harness produces two distinguishable "
                + "per-server states. Check that connectivity_check still MEASURES a refused socket "
                + "(it returns 1) rather than failing, and that both endpoints are being refused.");
            Assert.Equal(2, states.Select(s => s.ServerName).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // Let the setup measurements age out of the stale window, so the only fresh evidence in
            // play is what the cycle below produces.
            await Task.Delay(PastTheWindow);

            // -- one production cycle: the alert is due, and it measures cleanly on A ONLY ---------
            // B is not evaluated at all this cycle - an open breaker, a maintenance skip, a timeout.
            // This is exactly the shape the live service was in when it reaped 14 alerts on the one
            // server whose breaker was open.
            await svc.EvaluateSpecialAlertAsync(alert, connection, ServerA, defaults);
            svc.RecordEvaluationAttempt(alert.Id, DateTime.UtcNow);   // what the cycle stamps for a due alert

            // Nothing re-fired either state in the meantime, so both look stale to the reaper.
            var stale = DateTime.UtcNow.AddSeconds(-FrequencySeconds * 20);
            foreach (var s in svc.ActiveAlerts.Where(s => s.AlertId == alert.Id)) s.LastTriggered = stale;

            svc.ResolveCleared();

            var survivors = svc.ActiveAlerts.Where(s => s.AlertId == alert.Id)
                .Select(s => s.ServerName).ToList();

            // -- CONTROL, read first: the server that WAS measured this cycle is reaped ------------
            // Without this the subject assertion below could pass simply because the reaper did
            // nothing at all, and an instrument that cannot reap cannot testify that it did not.
            Assert.False(survivors.Any(n => string.Equals(n, ServerA, StringComparison.OrdinalIgnoreCase)),
                $"CONTROL FAILED: {ServerA} was measured cleanly this cycle and its state was stale, so "
                + "the reaper should have resolved it and did not. The subject assertion below is "
                + "therefore meaningless - a reaper that reaps nothing 'proves' every server safe. "
                + "Check ShouldAutoResolveAsCleared's window against FrequencySeconds on this fixture "
                + "and whether the measured path still stamps its per-server clock. Survivors: "
                + string.Join(", ", survivors));

            // -- SUBJECT: the server nothing looked at must survive --------------------------------
            Assert.True(survivors.Any(n => string.Equals(n, ServerB, StringComparison.OrdinalIgnoreCase)),
                $"{ServerB} was resolved as 'clear' on a cycle that never measured it. The only "
                + $"evaluation this cycle was against {ServerA}. Something let one server's freshness "
                + "answer for another's, which means a stamp is keyed by alert alone while the state "
                + "it guards is keyed by (alert, server).\n\n"
                + "WHAT TO CHECK: every read and write of the clock ResolveCleared consults - is each "
                + "one keyed by the same tuple as the _activeStates entry being judged? Survivors: "
                + string.Join(", ", survivors));
        }

        // =========================================================================================
        // H2 - an attempt that measured nothing must not count as a measurement
        // =========================================================================================

        [Fact]
        public async Task H2_anAttemptThatMeasuredNothingDoesNotCountAsAMeasurement()
        {
            var measurable = MeasurableAlert();

            // The same alert, on a cycle where the measurement does not come back. A queryMode with
            // no handler falls to the switch's default, which returns NotMeasured - the same "we got
            // no value from this server" outcome a timeout or a refused connection produces, reached
            // without depending on a real network timeout in a unit test.
            var unmeasurable = MeasurableAlert();
            unmeasurable.QueryMode = "no_handler_exists_for_this_query_mode";

            // ⚠ LOAD-BEARING, AND THIS TEST WAS BLIND WITHOUT IT. MeasurableAlert() mints a FRESH
            // GUID id on every call, so these two objects are two DIFFERENT ALERTS until this line.
            // With two ids, the failing cycle below stamps - or fails to stamp - under a key naming
            // an alert the subject assertion never reads, so NO regression in the attempt path can
            // reach the alert under test. What made the test pass was then something else entirely:
            // the setup measurement had simply aged out of the stale window. That is a restatement
            // of H1 ("the clock the reaper reads is keyed per alert AND server"), not a test of H2.
            //
            // Measured: with the ids distinct, an attempt-stamp compiled back into
            // EvaluateSpecialAlertAsync - the natural H2 regression - left this test GREEN. With
            // them shared it goes RED at the subject assertion, with the control passing. H2 is "a
            // failed attempt ON THIS ALERT, ON THIS SERVER stamps nothing", so the attempt that
            // fails must be the SAME (alert, server) pair the subject then reads. Do not split
            // these ids again.
            unmeasurable.Id = measurable.Id;

            using var svc = BuildEngine(DefinitionsFileContaining(measurable));
            svc.DryRun = true;
            var (connection, defaults) = Rig();

            // -- setup: the alert fires on A, from a real measurement ------------------------------
            await svc.EvaluateSpecialAlertAsync(measurable, connection, ServerA, defaults);

            // HAYSTACK before any needle.
            Assert.True(svc.ActiveAlerts.Any(s => s.AlertId == measurable.Id),
                "The setup measurement produced no alert state at all, so there is nothing for the "
                + "reaper to spare or take and this test proves nothing. Check that connectivity_check "
                + "still measures a refused socket and that the threshold still breaches on it.");

            // Age that measurement out of the stale window: the question is whether THIS cycle
            // measured anything, not whether some earlier one did.
            await Task.Delay(PastTheWindow);

            // -- the cycle: it attempted A, and got nothing back -----------------------------------
            svc.RecordEvaluationAttempt(measurable.Id, DateTime.UtcNow);  // stamped BEFORE the query, as production does
            await svc.EvaluateSpecialAlertAsync(unmeasurable, connection, ServerA, defaults);

            foreach (var s in svc.ActiveAlerts.Where(s => s.AlertId == measurable.Id))
                s.LastTriggered = DateTime.UtcNow.AddSeconds(-FrequencySeconds * 20);

            svc.ResolveCleared();

            // -- SUBJECT: an alert nobody could measure must not be resolved as clear ---------------
            Assert.True(svc.ActiveAlerts.Any(s => s.AlertId == measurable.Id),
                $"The alert was resolved as 'clear' on {ServerA} after a cycle that obtained NO value "
                + "from it. An attempt is not a measurement: the query failed, so nothing observed the "
                + "alert stop breaching, and resolving it turns an unmeasured alert into a clean Ok on "
                + "the wall. This is the route that produced the 2 silent reaps on a server with no "
                + "breaker window at all.\n\n"
                + "WHAT TO CHECK: which clock ShouldAutoResolveAsCleared is reading, and whether the "
                + "write to that clock happens before the query or only once a value is in hand.");

            // -- CONTROL: the same engine, same alert, a cycle that DID measure - and it is reaped ---
            // Proves the harness can still show a reap, so the survival asserted above is a decision
            // and not an inert instrument.
            await svc.EvaluateSpecialAlertAsync(measurable, connection, ServerA, defaults);
            foreach (var s in svc.ActiveAlerts.Where(s => s.AlertId == measurable.Id))
                s.LastTriggered = DateTime.UtcNow.AddSeconds(-FrequencySeconds * 20);
            svc.RecordEvaluationAttempt(measurable.Id, DateTime.UtcNow);
            svc.ResolveCleared();

            Assert.False(svc.ActiveAlerts.Any(s => s.AlertId == measurable.Id),
                "CONTROL FAILED: a cycle that DID measure this alert, on a state that was stale, did "
                + "not reap it. This harness cannot demonstrate a reap at all, so the subject "
                + "assertion above - that an unmeasured alert survives - is the silence of a broken "
                + "instrument rather than evidence about the fix. Check ShouldAutoResolveAsCleared's "
                + "window against this fixture's FrequencySeconds.");
        }

        // =========================================================================================
        // AXIS 3 of the census - is each collection's DECLARED scope actually TRUE?
        // =========================================================================================

        /// <summary>
        /// AlertStampKeyCensusTests' axis 1 proves every keyed collection DECLARES a scope. It
        /// cannot prove the declaration is true: a collection that is really per (alert, server)
        /// but declares PerAlert passes axis 1 exactly like an honest one, and that mistake is the
        /// defect this lane fixed, wearing a label.
        ///
        /// <para>So this test makes the class state its keying out loud. It drives ONE alert against
        /// TWO servers through the real engine, then reads the live keys back out of every
        /// collection the census enumerates. A per-(alert, server) collection must now hold TWO keys
        /// for that alert - one per server. A per-alert collection must hold ONE. A collection that
        /// claims to distinguish servers and produced a single key for two of them is not keyed the
        /// way it says, whatever its attribute reads.</para>
        ///
        /// <para>It shares the census's own enumerator rather than listing fields again, so the two
        /// axes cannot drift apart about what the population is.</para>
        /// </summary>
        [Fact]
        public async Task Axis3_twoServersOfOneAlertProduceTwoKeysInEveryPerServerCollection()
        {
            var alert = MeasurableAlert();
            using var svc = BuildEngine(DefinitionsFileContaining(alert));
            svc.DryRun = true;
            var (connection, defaults) = Rig();

            // Exercise both scopes: measure on two servers, and take one due-check for the alert.
            await svc.EvaluateSpecialAlertAsync(alert, connection, ServerA, defaults);
            await svc.EvaluateSpecialAlertAsync(alert, connection, ServerB, defaults);
            svc.RecordEvaluationAttempt(alert.Id, DateTime.UtcNow);

            var population = AlertStampKeyCensusTests.KeyedCollections();
            var mine = alert.Id.ToLowerInvariant();

            var report = new System.Text.StringBuilder();
            report.AppendLine($"BEHAVIOURAL KEY CENSUS - one alert ({alert.Id}) driven against {ServerA} and {ServerB}:");
            var observed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var unobserved = new List<string>();
            var unreadable = new List<string>();

            foreach (var (field, scope) in population)
            {
                string scopeText = scope?.ToString() ?? "UNCLASSIFIED";
                List<string> keys;
                try
                {
                    var dict = field.GetValue(svc) as System.Collections.IDictionary;
                    if (dict == null)
                    {
                        unreadable.Add($"{field.Name} - not readable as IDictionary");
                        report.AppendLine($"   {field.Name,-28} {scopeText,-16} *** COULD NOT READ ***");
                        continue;
                    }
                    keys = dict.Keys.Cast<object>().Select(k => k?.ToString() ?? "")
                        .Where(k => k.Contains(mine, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(k => k, StringComparer.Ordinal).ToList();
                }
                catch (Exception ex)
                {
                    unreadable.Add($"{field.Name} - {ex.GetType().Name}: {ex.Message}");
                    report.AppendLine($"   {field.Name,-28} {scopeText,-16} *** COULD NOT READ ***");
                    continue;
                }

                if (keys.Count == 0)
                {
                    unobserved.Add(field.Name);
                    report.AppendLine($"   {field.Name,-28} {scopeText,-16} (not exercised by this drive)");
                }
                else
                {
                    observed[field.Name] = keys;
                    report.AppendLine($"   {field.Name,-28} {scopeText,-16} {keys.Count} key(s): {string.Join(" | ", keys)}");
                }
            }
            report.AppendLine($"   -- observed {observed.Count}, not exercised {unobserved.Count}, COULD NOT READ {unreadable.Count} --");
            var text = report.ToString();
            // Printed on PASS as well as on failure: a census whose distribution is only
            // visible when it fails cannot be audited on the day it matters.
            _out.WriteLine(text);

            // HAYSTACK, before any needle.
            Assert.True(population.Count > 0,
                "The shared census enumerator returned NO collections, so this corroboration measured "
                + "nothing. Check AlertStampKeyCensusTests.KeyedCollections.\n" + text);

            // COULD NOT READ never shares an answer with FOUND NOTHING.
            Assert.True(unreadable.Count == 0,
                "COULD NOT READ " + unreadable.Count + " collection(s), so this test is silent about "
                + "them rather than clearing them:\n   " + string.Join("\n   ", unreadable)
                + "\n\nWHAT TO CHECK: whether those fields are still dictionaries this reader can "
                + "enumerate.\n" + text);

            // SPECIMEN CONTROL: the drive must actually have populated the known-good per-server
            // collections with two keys. If it did not, every "correctly keyed" below is the verdict
            // of an instrument that was handed nothing.
            foreach (var control in new[] { "_activeStates", "_lastNotified" })
            {
                Assert.True(observed.ContainsKey(control) && observed[control].Count == 2,
                    $"SPECIMEN CONTROL FAILED: {control} should hold exactly two keys after one alert "
                    + $"was measured on two servers, and holds "
                    + (observed.TryGetValue(control, out var got) ? got.Count.ToString() : "none")
                    + ". This drive is not exercising the engine the way this test assumes, so its "
                    + "findings about every other collection are worthless. Check that both "
                    + "evaluations fired.\n" + text);
            }

            // THE NEEDLE: does each collection key the way it says it does?
            var wrong = new List<string>();
            foreach (var (field, scope) in population)
            {
                if (!observed.TryGetValue(field.Name, out var keys)) continue;
                var expected = scope == AlertKeyScopeKind.PerAlertServer ? 2 : 1;
                if (keys.Count != expected)
                    wrong.Add($"{field.Name} declares {scope} so one alert on two servers should leave "
                        + $"{expected} key(s); it left {keys.Count}: {string.Join(" | ", keys)}");
            }

            Assert.True(wrong.Count == 0,
                "A COLLECTION IS NOT KEYED THE WAY IT DECLARES:\n   " + string.Join("\n   ", wrong)
                + "\n\nA per-(alert, server) collection that produced ONE key for two servers cannot "
                + "tell those servers apart, so anything that reads it is judging one server by "
                + "another's evidence - the defect this lane exists for. A per-alert collection that "
                + "produced TWO is keyed more finely than it claims, and anything reading it by alert "
                + "id alone will miss entries.\n\n"
                + "WHAT TO CHECK: where that collection's keys are composed, and whether they come "
                + "from AlertEvaluationService.StateKey. Then decide which is wrong, the keying or "
                + "the declaration - do not simply change the number in this test.\n" + text);
        }

        // =========================================================================================
        // FIX ROUND 2 (2026-09-17) - the owner's two rulings, driven through the REAL engine
        //
        //   R1  "A completed query counts." A standard query that COMPLETES without throwing stamps
        //       the measurement clock, NULL included. Without it, an alert whose query returns NULL
        //       when the server is healthy (blocking_process, agent_job_long_running) never clears.
        //   I1  Stamped on every route on which the standard query completed, and on NO route on
        //       which it threw, timed out, was cancelled, was skipped or never ran.
        //   I2  "Prune in fix round 2." No per-(alert, server) key survives once the enabled set no
        //       longer evaluates it; a key the enabled set DOES evaluate - unreachable or
        //       breaker-skipped this cycle included - is never pruned. And an EMPTY enabled server set
        //       or an EMPTY enabled alert set is not evidence that anything was removed: it never
        //       prunes, never resolves a state and never touches history.
        // =========================================================================================

        /// <summary>
        /// R1 on both shapes a completed query can hand back with no value. <c>DBNull</c> is the one a
        /// healthy server really produces for blocking_process and agent_job_long_running (a MAX over
        /// an empty set is one row holding NULL), and it is the case that exercises the DBNull half of
        /// the mapping in ExecuteAlertQueryAsync. <c>null</c> is what ExecuteScalarAsync returns when
        /// a query yields no rows at all.
        /// </summary>
        [Theory]
        [InlineData("DBNull")]
        [InlineData("null")]
        public async Task R1_aStandardQueryThatCompletesWithNullStampsTheClock_soAStaleStateIsCleared(string completedScalar)
        {
            object? scalar = completedScalar == "DBNull" ? DBNull.Value : null;

            var alert = StandardAlert(FrequencySeconds);
            using var svc = BuildEngine(DefinitionsFileContaining(alert));
            svc.DryRun = true;
            var (connection, defaults) = Rig();
            var answers = new QueryAnswers(svc);

            // -- setup: the alert fires on A from a real breaching value ---------------------------
            answers.Set(ServerA, () => Task.FromResult<object?>(5.0));
            await svc.ThrottledEvaluateAsync(alert, connection, ServerA, defaults, CancellationToken.None);

            // HAYSTACK before any needle.
            var fired = svc.ActiveAlerts.FirstOrDefault(s => s.AlertId == alert.Id);
            Assert.True(fired != null,
                "The setup cycle produced no alert state, so there is nothing for the reaper to take "
                + "or spare and this test proves nothing. Check that ExecuteAlertQueryAsync still "
                + "consults StandardQueryOverrideForTests, and that a value of 5 still breaches Warning 0 "
                + "on the standard path.");
            var hitsBefore = fired!.HitCount;
            var valueBefore = fired.LastValue;

            // Age the setup stamp out of the stale window: the question is whether the NULL cycle
            // below stamps, not whether the setup cycle did.
            await Task.Delay(PastTheWindow);
            AgeLastTriggered(svc, alert);

            // -- CONTROL: with only the aged setup stamp in play, the reaper must SPARE the state ---
            svc.ResolveCleared();
            Assert.True(svc.ActiveAlerts.Any(s => s.AlertId == alert.Id),
                "CONTROL FAILED: the reaper resolved this alert BEFORE the NULL cycle ran, when the only "
                + "measurement stamp in play was older than the stale window. The reap asserted below "
                + "would then say nothing about the NULL cycle. Check PastTheWindow against "
                + "ShouldAutoResolveAsCleared's window for this fixture's FrequencySeconds.");

            // -- the cycle: the standard query COMPLETES and hands back no value --------------------
            answers.Set(ServerA, () => Task.FromResult<object?>(scalar));
            await svc.ThrottledEvaluateAsync(alert, connection, ServerA, defaults, CancellationToken.None);

            // Still no comparison and no state change: a NULL is not a reading.
            var afterNull = svc.ActiveAlerts.FirstOrDefault(s => s.AlertId == alert.Id);
            Assert.True(afterNull != null && afterNull.Status == AlertStatus.Active
                        && afterNull.HitCount == hitsBefore && afterNull.LastValue == valueBefore,
                $"A standard query whose scalar was {completedScalar} changed the alert's state (status "
                + (afterNull?.Status.ToString() ?? "GONE") + ", hits " + hitsBefore + " then "
                + (afterNull?.HitCount.ToString() ?? "n/a") + "). A NULL is not a reading and nothing "
                + "may be compared on it. WHAT TO CHECK: the raw == null branch of "
                + "EvaluateAlertOnServerAsync, and whether anything between the query and that branch now "
                + "treats a NULL as a value.");
            var nullFailure = svc.EvaluationFailures.FirstOrDefault(f => f.AlertId == alert.Id
                && SameServer(f.ServerName, ServerA));
            Assert.False(svc.HasEvaluationFailure(ServerA),
                $"A standard query that COMPLETED with a {completedScalar} scalar was recorded as an "
                + "evaluation failure ('" + (nullFailure?.ErrorSummary ?? "no record for this alert") + "'). "
                + "The server answered. WHAT TO CHECK: whether ExecuteAlertQueryAsync still maps BOTH a "
                + "null and a DBNull scalar to no value before Convert.ToDouble (an InvalidCastException "
                + "above points there), and whether the NULL route still reaches ClearEvaluationFailure "
                + "and not RecordEvaluationFailure.");

            AgeLastTriggered(svc, alert);
            svc.ResolveCleared();

            // -- SUBJECT: the server answered, so the stale state is cleared -----------------------
            Assert.False(svc.ActiveAlerts.Any(s => s.AlertId == alert.Id),
                $"The alert stayed on the wall after its standard query COMPLETED against {ServerA} with a "
                + completedScalar + " scalar, with the alert's state stale. By the owner's ruling of 2026-09-17 a "
                + "completed query counts as a measurement for the auto-clear, NULL included, and the "
                + "control above proved the reaper spares this state while nothing has answered. An "
                + "alert whose query returns NULL when the server is healthy - blocking_process, "
                + "agent_job_long_running - otherwise never auto-resolves.\n\n"
                + "WHAT TO CHECK: whether ExecuteAlertQueryAsync returned null for this scalar, whether "
                + "EvaluateAlertOnServerAsync reaches RecordServerEvaluation on that route, and whether "
                + "that write is keyed by this (alert, server).");
        }

        [Fact]
        public async Task I1_aStandardQueryThatThrowsStampsNothing_soAStaleStateSurvives()
        {
            var alert = StandardAlert(FrequencySeconds);
            using var svc = BuildEngine(DefinitionsFileContaining(alert));
            svc.DryRun = true;
            var (connection, defaults) = Rig();
            var answers = new QueryAnswers(svc);

            // -- setup: the alert fires on A from a real breaching value ---------------------------
            answers.Set(ServerA, () => Task.FromResult<object?>(5.0));
            await svc.ThrottledEvaluateAsync(alert, connection, ServerA, defaults, CancellationToken.None);

            Assert.True(svc.ActiveAlerts.Any(s => s.AlertId == alert.Id),
                "The setup cycle produced no alert state, so there is nothing for the reaper to spare and "
                + "this test proves nothing. Check that ExecuteAlertQueryAsync still consults "
                + "StandardQueryOverrideForTests, and that a value of 5 still breaches Warning 0.");

            // Age the setup stamp out of the stale window, so any fresh stamp below came from a
            // cycle that threw.
            await Task.Delay(PastTheWindow);

            // Three ways a standard query fails to complete. A REAL SqlException from a refused
            // socket lands in the SqlException catch; a timeout and a cancellation land in the
            // general catch. Each is its own cycle, judged on its own.
            var failures = new (string ExpectedType, Func<Task<object?>> Answer)[]
            {
                ("SqlException", RefusedSqlConnectionAsync),
                ("TimeoutException", () => throw new TimeoutException("the standard query timed out (test seam)")),
                ("OperationCanceledException", () => throw new OperationCanceledException("the standard query was cancelled (test seam)")),
            };

            foreach (var (expectedType, answer) in failures)
            {
                answers.Set(ServerA, answer);
                await svc.ThrottledEvaluateAsync(alert, connection, ServerA, defaults, CancellationToken.None);

                // ROUTE CHECK: the failure record names the exception type, which proves this cycle
                // really threw inside the query and landed in a catch.
                var recorded = svc.EvaluationFailures.FirstOrDefault(f => f.AlertId == alert.Id
                    && SameServer(f.ServerName, ServerA));
                Assert.True(recorded != null
                            && recorded.ErrorSummary.StartsWith(expectedType + ":", StringComparison.Ordinal),
                    $"ROUTE CHECK FAILED for {expectedType}: the cycle recorded "
                    + (recorded == null ? "no evaluation failure" : "the failure '" + recorded.ErrorSummary + "'")
                    + ", so whatever the subject below reports is not about a query that threw that "
                    + "exception. Check that StandardQueryOverrideForTests still lets an exception escape "
                    + "ExecuteAlertQueryAsync and, for SqlException, that TCP port 1 on 127.0.0.1 still "
                    + "refuses a connection.");

                AgeLastTriggered(svc, alert);
                svc.ResolveCleared();

                // -- SUBJECT: nothing answered, so nothing may clear the state ---------------------
                Assert.True(svc.ActiveAlerts.Any(s => s.AlertId == alert.Id),
                    $"The alert was resolved as 'clear' on {ServerA} after a cycle whose standard query "
                    + $"threw {expectedType}. The only other stamp in play was older than the stale "
                    + "window, so something wrote the measurement clock on a route where the server did "
                    + "not answer - the standard-path twin of the H2 defect.\n\n"
                    + "WHAT TO CHECK: where the standard path's RecordServerEvaluation sits relative to "
                    + "the ExecuteAlertQueryAsync call and the two catches in EvaluateAlertOnServerAsync, "
                    + "and whether anything else that runs on a failed attempt writes the same clock.");
            }

            // -- CONTROL: the same engine and alert, a cycle that DID answer - and it is reaped -----
            answers.Set(ServerA, () => Task.FromResult<object?>(5.0));
            await svc.ThrottledEvaluateAsync(alert, connection, ServerA, defaults, CancellationToken.None);
            AgeLastTriggered(svc, alert);
            svc.ResolveCleared();

            Assert.False(svc.ActiveAlerts.Any(s => s.AlertId == alert.Id),
                "CONTROL FAILED: a cycle whose standard query completed with a value, on a state then "
                + "made stale, was not reaped. This harness cannot show a reap, so every survival asserted "
                + "above is the silence of an inert instrument and not evidence about the throw routes. "
                + "Check ShouldAutoResolveAsCleared's window against this fixture's FrequencySeconds.");
        }

        [Fact]
        public async Task I2_aServerRemovedFromMonitoringLeavesNoKeyInAnyPerServerCollection()
        {
            // THE POPULATION, from the census's own reflection enumerator - never a hand list.
            var perServer = PerServerCollections();
            Assert.True(perServer.Count > 0,
                "AlertStampKeyCensusTests.KeyedCollections() returned no PerAlertServer collection, so "
                + "'no key in any collection' below would be true of nothing. Check that enumerator and "
                + "the [AlertKeyScope] declarations on AlertEvaluationService.");

            // A backslash and a comma in every name, on purpose: live keys must be COMPOSED from the
            // enabled sets, never parsed back out of a key.
            const string Stays = @"STAMP-STAYS\SQL1,1433";
            const string GoneActive = @"STAMP-GONE\SQL1,1433";
            const string GoneAcknowledged = @"STAMP-ACKED\SQL1,1433";
            var servers = new[] { Stays, GoneActive, GoneAcknowledged };

            var alert = StandardAlert(3 * 60 * 60);   // a nine-hour stale window: nothing here is reaped for age
            alert.Escalate = true;                     // so _escalatedEpisodes is populated as well
            alert.EscalationAfterMinutes = 0;          // escalates on its first firing cycle

            var (connections, byServer) = ConnectionsStore(servers);
            var history = new AlertHistoryService(NullLogger<AlertHistoryService>.Instance);
            using var svc = BuildEngine(CycleDefinitionsFileContaining(alert), connections, history);
            // Escalation is gated on !_dryRun, and _escalatedEpisodes is one of the collections under
            // test, so the real dispatcher is on. What proves no channel in this test tree can page
            // anybody is SpecialAlertEscalationTests.Preflight_noOutboundChannelIsEnabled_soAnEscalationTestCannotPageAnybody.
            svc.DryRun = false;
            var answers = new QueryAnswers(svc);

            try
            {
                AssertEnabledServersAre(connections, servers);

                foreach (var server in servers)
                    await SeedEveryPerServerCollectionAsync(svc, answers, alert, byServer[server], server);
                svc.AcknowledgeAlert(alert.Id, GoneAcknowledged);

                // SPECIMEN: every per-server collection holds a key for every seeded server. A
                // collection this drive never populated would read "no key" after the prune whether
                // or not the prune ever touched it.
                var before = KeyCensus(svc, perServer, alert.Id, servers);
                _out.WriteLine("BEFORE the removal cycle:\n" + before.Report);
                AssertReadable(before);
                AssertEveryCollectionWasExercised(before);

                var activeState = svc.ActiveAlerts.First(s => s.AlertId == alert.Id && SameServer(s.ServerName, GoneActive));
                var acknowledgedState = svc.ActiveAlerts.First(s => s.AlertId == alert.Id && SameServer(s.ServerName, GoneAcknowledged));
                Assert.True(activeState.Status == AlertStatus.Active && acknowledgedState.Status == AlertStatus.Acknowledged,
                    "HARNESS: the two states that will be removed are not one Active and one Acknowledged, so "
                    + "this test would not cover the close of an ACKNOWLEDGED state. Check AcknowledgeAlert.");
                Assert.True(servers.All(s => HistoryRows(history, alert.Id, s).Any(IsOpen)),
                    "HARNESS: the seeding fire left no open history row for some server, so 'its history row "
                    + "was resolved' below could not be observed. Check that DryRun is off while seeding.");

                // -- the operator removes two servers from monitoring ------------------------------
                foreach (var gone in new[] { GoneActive, GoneAcknowledged })
                {
                    var removal = connections.RemoveConnection(byServer[gone].Id);
                    Assert.True(removal.Succeeded, $"HARNESS: could not remove {gone} from the connection store: {removal.Reason}");
                }
                AssertEnabledServersAre(connections, Stays);

                // The server that stays is attempted and fails this cycle, so it measures nothing and
                // must keep every key it had, its evaluation failure included.
                answers.Set(Stays, () => throw new TimeoutException("the standard query timed out (test seam)"));

                await svc.EvaluateAllAsync();

                var after = KeyCensus(svc, perServer, alert.Id, servers);
                _out.WriteLine("AFTER the removal cycle:\n" + after.Report);
                AssertReadable(after);

                // -- CONTROL: the server still monitored keeps every key and its open alert --------
                var lostByStays = after.Held.Where(h => SameServer(h.Key.Server, Stays) && !h.Value)
                    .Select(h => h.Key.Field).ToList();
                Assert.True(lostByStays.Count == 0
                            && svc.ActiveAlerts.Any(s => s.AlertId == alert.Id && SameServer(s.ServerName, Stays))
                            && HistoryRows(history, alert.Id, Stays).Any(IsOpen),
                    $"CONTROL FAILED: {Stays} is still in the enabled set, and after the cycle it has lost its "
                    + "key from [" + string.Join(", ", lostByStays) + "], or its open alert, or its open "
                    + "history row. A prune that removes live keys makes every 'the removed server has no "
                    + "key' below meaningless. WHAT TO CHECK: how PruneUnmonitoredAlertKeys composes the "
                    + "live set.\n" + after.Report);

                // -- SUBJECT: the removed servers have no key in any per-(alert, server) collection -
                var survivors = after.Held.Where(h => !SameServer(h.Key.Server, Stays) && h.Value)
                    .Select(h => h.Key.Field + " still holds " + h.Key.Server).ToList();
                Assert.True(survivors.Count == 0,
                    "A SERVER REMOVED FROM MONITORING STILL HAS KEYS after a full evaluation cycle:\n   "
                    + string.Join("\n   ", survivors)
                    + "\n\nNothing will evaluate these keys again. An alert state left behind stays on the "
                    + "wall for ever, because its measurement clock is never stamped and the reaper "
                    + "declines; every other entry is a retention nothing owns.\n\n"
                    + "WHAT TO CHECK: whether PruneUnmonitoredAlertKeys covers each collection named above, "
                    + "and whether EvaluateAllAsync still calls it past the configuration guards.\n"
                    + after.Report);

                foreach (var (label, state, server) in new[]
                         {
                             ("Active", activeState, GoneActive),
                             ("Acknowledged", acknowledgedState, GoneAcknowledged),
                         })
                {
                    var resolvedAt = state.ResolvedAt.HasValue ? state.ResolvedAt.Value.ToString("o") : "never";
                    Assert.True(state.Status == AlertStatus.Resolved && state.ResolvedAt.HasValue,
                        $"The {label} state on removed server {server} left the engine without being closed "
                        + $"(status {state.Status}, resolved at {resolvedAt}). "
                        + "WHAT TO CHECK: whether the prune closes a state the way ResolveCleared closes one.");
                    Assert.False(svc.ActiveAlerts.Any(s => s.AlertId == alert.Id && SameServer(s.ServerName, server)),
                        $"The {label} alert on removed server {server} is still in ActiveAlerts after the cycle.");
                    var rows = HistoryRows(history, alert.Id, server);
                    Assert.True(rows.Count > 0 && !rows.Any(IsOpen),
                        $"The history row for the {label} alert on removed server {server} is still open "
                        + "(" + string.Join(", ", rows.Select(r => r.Status)) + "), so the Alerts page, "
                        + "which reads history, keeps showing an alert nothing monitors. WHAT TO CHECK: "
                        + "whether the prune calls AlertHistoryService.ResolveAlert for each state it closes.");
                }
            }
            finally
            {
                // Leave the shared history database as this test found it.
                foreach (var server in servers) history.ResolveAlert(alert.Id, server);
                history.Dispose();
            }
        }

        [Fact]
        public async Task I2_anEnabledServerThatWasNotMeasuredThisCycleIsNeverPruned()
        {
            var perServer = PerServerCollections();
            Assert.True(perServer.Count > 0,
                "AlertStampKeyCensusTests.KeyedCollections() returned no PerAlertServer collection, so "
                + "'every key survives' below would be true of nothing. Check that enumerator and the "
                + "[AlertKeyScope] declarations on AlertEvaluationService.");

            const string Unreachable = @"STAMP-DOWN\SQL1,1433";
            const string BreakerOpen = @"STAMP-BREAKER\SQL1,1433";
            const string Removed = @"STAMP-REMOVED\SQL1,1433";
            var servers = new[] { Unreachable, BreakerOpen, Removed };

            var alert = StandardAlert(3 * 60 * 60);   // nothing here is reaped for age
            alert.Escalate = true;
            alert.EscalationAfterMinutes = 0;

            var (connections, byServer) = ConnectionsStore(servers);
            var history = new AlertHistoryService(NullLogger<AlertHistoryService>.Instance);
            var breaker = new ServerCircuitBreakerService(NullLogger<ServerCircuitBreakerService>.Instance, audit: null);
            using var svc = BuildEngine(CycleDefinitionsFileContaining(alert), connections, history, breaker);
            svc.DryRun = false;   // for the reason, and the preflight, given in the removal test above
            var answers = new QueryAnswers(svc);

            try
            {
                AssertEnabledServersAre(connections, servers);
                foreach (var server in servers)
                    await SeedEveryPerServerCollectionAsync(svc, answers, alert, byServer[server], server);

                var before = KeyCensus(svc, perServer, alert.Id, servers);
                _out.WriteLine("BEFORE the cycle:\n" + before.Report);
                AssertReadable(before);
                AssertEveryCollectionWasExercised(before);

                // Open one server's circuit the way the shipped breaker opens: consecutive failures.
                for (var i = 0; i < 50 && breaker.ShouldAttempt(BreakerOpen); i++)
                    breaker.RecordFailure(BreakerOpen, ServerAnswerClassifierTests.MakeSqlException((11001, 20)));
                Assert.True(!breaker.ShouldAttempt(BreakerOpen) && breaker.ShouldAttempt(Unreachable),
                    $"HARNESS: expected {BreakerOpen}'s circuit open and {Unreachable}'s closed before the "
                    + "cycle. Without that, this test does not cover a breaker-skipped server.");

                var removal = connections.RemoveConnection(byServer[Removed].Id);
                Assert.True(removal.Succeeded, $"HARNESS: could not remove {Removed} from the connection store: {removal.Reason}");
                AssertEnabledServersAre(connections, Unreachable, BreakerOpen);

                answers.Set(Unreachable, RefusedSqlConnectionAsync);
                answers.Set(BreakerOpen, () => Task.FromResult<object?>(0.0));   // never asked while its circuit is open
                var unreachableCallsBefore = answers.Calls(Unreachable);
                var breakerCallsBefore = answers.Calls(BreakerOpen);

                await svc.EvaluateAllAsync();

                var after = KeyCensus(svc, perServer, alert.Id, servers);
                _out.WriteLine("AFTER the cycle:\n" + after.Report);
                AssertReadable(after);

                // ROUTE CHECKS: the cycle really attempted and FAILED one server, and really SKIPPED the
                // other. Without both, "not measured this cycle" is an assumption, not a fact.
                var downFailure = svc.EvaluationFailures.FirstOrDefault(f => f.AlertId == alert.Id
                    && SameServer(f.ServerName, Unreachable));
                Assert.True(answers.Calls(Unreachable) > unreachableCallsBefore
                            && downFailure != null
                            && downFailure.ErrorSummary.StartsWith("SqlException:", StringComparison.Ordinal),
                    $"ROUTE CHECK FAILED: {Unreachable} was not attempted this cycle, or its attempt did not "
                    + "fail with a SqlException (recorded: " + (downFailure?.ErrorSummary ?? "nothing") + "). "
                    + "Check that the alert was due and that TCP port 1 on 127.0.0.1 still refuses.");
                Assert.True(answers.Calls(BreakerOpen) == breakerCallsBefore,
                    $"ROUTE CHECK FAILED: {BreakerOpen}'s query was asked although its circuit was open, so "
                    + "this cycle did not skip it and the breaker-skip route is not covered.");

                // -- CONTROL: the prune really ran this cycle - the removed server has no key -------
                var removedStillHeld = after.Held.Where(h => SameServer(h.Key.Server, Removed) && h.Value)
                    .Select(h => h.Key.Field).ToList();
                Assert.True(removedStillHeld.Count == 0,
                    $"CONTROL FAILED: {Removed} left the enabled set, yet after the cycle it still has a key in "
                    + "[" + string.Join(", ", removedStillHeld) + "]. The prune did not run, or did not "
                    + "reach these collections, so the survival asserted below is not a decision the prune "
                    + "made. WHAT TO CHECK: whether EvaluateAllAsync calls PruneUnmonitoredAlertKeys past "
                    + "its configuration guards.\n" + after.Report);

                // -- SUBJECT: both still-enabled servers keep every key and their open alerts -------
                var lost = after.Held.Where(h => !SameServer(h.Key.Server, Removed) && !h.Value)
                    .Select(h => h.Key.Field + " lost " + h.Key.Server).ToList();
                var closed = new[] { Unreachable, BreakerOpen }
                    .Where(s => !svc.ActiveAlerts.Any(a => a.AlertId == alert.Id && SameServer(a.ServerName, s)
                                                          && a.Status == AlertStatus.Active)
                                || !HistoryRows(history, alert.Id, s).Any(IsOpen))
                    .ToList();
                Assert.True(lost.Count == 0 && closed.Count == 0,
                    "A SERVER STILL IN THE ENABLED SET WAS PRUNED because it produced no measurement this "
                    + "cycle.\n   lost keys: " + (lost.Count == 0 ? "none" : string.Join("; ", lost))
                    + "\n   alert or history row closed: " + (closed.Count == 0 ? "none" : string.Join("; ", closed))
                    + "\n\nAn unreachable or breaker-skipped server is still monitored. Closing its alert on "
                    + "that basis is the H1 defect in another costume: an alert cleared on a server nothing "
                    + "measured.\n\n"
                    + "WHAT TO CHECK: what PruneUnmonitoredAlertKeys treats as live - the enabled alerts "
                    + "and servers, or something that depends on what this cycle managed to measure.\n"
                    + after.Report);
            }
            finally
            {
                foreach (var server in servers) history.ResolveAlert(alert.Id, server);
                history.Dispose();
            }
        }

        /// <summary>
        /// The prune's one safety property: an EMPTY enabled server set or an EMPTY enabled alert set
        /// never prunes, never resolves a state and never touches history. ServerConnectionManager
        /// reads a damaged server-connections store as an empty enabled list, and EvaluateAllAsync
        /// cannot tell that from "nothing configured", so it must return before the prune on both.
        ///
        /// <para>Two cases, each on its own engine, and each keeps the OTHER set non-empty so it can
        /// only be stopped by its own guard: zero enabled servers (every connection removed) with the
        /// alert enabled, and zero enabled alerts (the alert disabled) with its servers enabled. So
        /// the test goes red if the prune moves above either guard. Each case ends with a CONTROL on
        /// the same engine: a NON-empty set that still excludes the seeded keys does close them, so
        /// the survival asserted first is not the silence of a prune that never runs.</para>
        /// </summary>
        [Fact]
        public async Task I2_anEmptyEnabledSetIsNotEvidenceThatAnythingWasRemoved()
        {
            var perServer = PerServerCollections();
            Assert.True(perServer.Count > 0,
                "AlertStampKeyCensusTests.KeyedCollections() returned no PerAlertServer collection, so "
                + "'every key survives' below would be true of nothing. Check that enumerator and the "
                + "[AlertKeyScope] declarations on AlertEvaluationService.");

            await AssertAnEmptyEnabledSetClosesNothingAsync(perServer, EmptiedSet.Servers);
            await AssertAnEmptyEnabledSetClosesNothingAsync(perServer, EmptiedSet.Alerts);
        }

        private enum EmptiedSet { Servers, Alerts }

        private async Task AssertAnEmptyEnabledSetClosesNothingAsync(IReadOnlyList<FieldInfo> perServer, EmptiedSet emptied)
        {
            var tag = emptied == EmptiedSet.Servers ? "NOSERVER" : "NOALERT";
            var first = $@"STAMP-{tag}-1\SQL1,1433";
            var second = $@"STAMP-{tag}-2\SQL1,1433";
            var outsider = $@"STAMP-{tag}-OUTSIDER\SQL1,1433";   // never seeded; the Servers control adds it
            var servers = new[] { first, second };

            var alert = StandardAlert(3 * 60 * 60);   // nothing here is reaped for age
            alert.Escalate = true;
            alert.EscalationAfterMinutes = 0;

            // Disabled until the Alerts control enables it, so that control's enabled alert set is
            // non-empty and still excludes the seeded alert. It is never seeded.
            var bystander = StandardAlert(3 * 60 * 60);
            bystander.Enabled = false;

            var definitionsPath = CycleDefinitionsFileContaining(alert, bystander);
            var definitions = new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, definitionsPath);
            var (connections, byServer) = ConnectionsStore(servers);
            var history = new AlertHistoryService(NullLogger<AlertHistoryService>.Instance);
            using var svc = BuildEngine(definitionsPath, connections, history, definitions: definitions);
            svc.DryRun = false;   // for the reason, and the preflight, given in the removal test above
            var answers = new QueryAnswers(svc);

            try
            {
                AssertEnabledServersAre(connections, servers);
                foreach (var server in servers)
                    await SeedEveryPerServerCollectionAsync(svc, answers, alert, byServer[server], server);

                // SPECIMEN: every per-server collection holds a key for both servers, and both carry an
                // Active state and an open history row, before the cycle under test.
                var before = KeyCensus(svc, perServer, alert.Id, servers);
                _out.WriteLine($"[{emptied}] BEFORE the empty-set cycle:\n" + before.Report);
                AssertReadable(before);
                AssertEveryCollectionWasExercised(before);
                Assert.True(servers.All(s => svc.ActiveAlerts.Any(a => a.AlertId == alert.Id && SameServer(a.ServerName, s)
                                                                      && a.Status == AlertStatus.Active)
                                             && HistoryRows(history, alert.Id, s).Any(IsOpen)),
                    $"HARNESS [{emptied}]: the seeding drive did not leave an Active state and an open history "
                    + "row on both servers, so their survival below could not be observed. Check that DryRun "
                    + "is off while seeding.");

                // -- empty ONE enabled set, keeping the other non-empty ---------------------------------
                if (emptied == EmptiedSet.Servers)
                {
                    foreach (var server in servers)
                    {
                        var removal = connections.RemoveConnection(byServer[server].Id);
                        Assert.True(removal.Succeeded, $"HARNESS: could not remove {server} from the connection store: {removal.Reason}");
                    }
                    AssertEnabledServersAre(connections);
                }
                else
                {
                    var disabled = definitions.SetAlertEnabled(alert.Id, false);
                    Assert.True(disabled == StoreWriteOutcome.Saved,
                        $"HARNESS: AlertDefinitionService.SetAlertEnabled could not disable the seeded alert ({disabled}).");
                    AssertEnabledServersAre(connections, servers);
                }

                var enabledAlertIds = definitions.GetEnabledAlerts().Select(a => a.Id).ToList();
                var expectedAlertIds = emptied == EmptiedSet.Servers ? new List<string> { alert.Id } : new List<string>();
                Assert.True(definitions.GetGlobalDefaults().Enabled && enabledAlertIds.SequenceEqual(expectedAlertIds),
                    $"HARNESS [{emptied}]: before the cycle the engine is "
                    + (definitions.GetGlobalDefaults().Enabled ? "on" : "OFF") + " with enabled alerts ["
                    + string.Join(", ", enabledAlertIds) + "], and this case expected it on with ["
                    + string.Join(", ", expectedAlertIds) + "]. Otherwise the cycle stops at a different "
                    + "return than the guard this case is about.");

                await svc.EvaluateAllAsync();

                var after = KeyCensus(svc, perServer, alert.Id, servers);
                _out.WriteLine($"[{emptied}] AFTER the empty-set cycle:\n" + after.Report);
                AssertReadable(after);

                // -- SUBJECT: nothing was closed, pruned or resolved ----------------------------------
                var lost = after.Held.Where(h => !h.Value)
                    .Select(h => h.Key.Field + " lost " + h.Key.Server).ToList();
                var closed = servers
                    .Where(s => !svc.ActiveAlerts.Any(a => a.AlertId == alert.Id && SameServer(a.ServerName, s)
                                                           && a.Status == AlertStatus.Active)
                                || !HistoryRows(history, alert.Id, s).Any(IsOpen))
                    .ToList();
                Assert.True(lost.Count == 0 && closed.Count == 0,
                    $"A CYCLE WITH AN EMPTY ENABLED {(emptied == EmptiedSet.Servers ? "SERVER" : "ALERT")} SET "
                    + "CLOSED OR PRUNED WHAT IT COULD NOT SEE."
                    + "\n   lost keys: " + (lost.Count == 0 ? "none" : string.Join("; ", lost))
                    + "\n   alert or history row closed: " + (closed.Count == 0 ? "none" : string.Join("; ", closed))
                    + "\n\nAn empty enabled set is not evidence that anything was removed. "
                    + "ServerConnectionManager reads a DAMAGED server-connections store as an empty enabled "
                    + "list, and a cycle that closes on it takes every alert off the wall and resolves every "
                    + "open history row across the estate.\n\n"
                    + "WHAT TO CHECK: where PruneUnmonitoredAlertKeys, ReconcileEvaluationFailures and "
                    + "ResolveCleared are called in EvaluateAllAsync relative to its no-server return and its "
                    + "no-alert return, and whether anything else that runs before those returns closes a "
                    + "state, removes a key or writes history. If this was seen on a live service, also check "
                    + "whether the enabled set read empty because a store is damaged "
                    + "(ServerConnectionManager.IsStoreDamaged, AlertDefinitionService.IsStoreDamaged).\n"
                    + after.Report);

                // -- CONTROL: on this engine a NON-empty set that excludes the seeded keys closes them -
                string controlSet;
                if (emptied == EmptiedSet.Servers)
                {
                    var added = connections.AddConnection(new ServerConnection
                    {
                        Id = Guid.NewGuid().ToString(),
                        ServerNames = outsider,
                        UseWindowsAuthentication = true,
                        ConnectionTimeout = 2,
                        IsEnabled = true,
                    });
                    Assert.True(added.Succeeded, $"HARNESS: could not add {outsider} to the connection store: {added.Reason}");
                    AssertEnabledServersAre(connections, outsider);
                    answers.Set(outsider, () => Task.FromResult<object?>(DBNull.Value));
                    controlSet = "enabled servers [" + outsider + "], the seeded alert enabled";
                }
                else
                {
                    var enabled = definitions.SetAlertEnabled(bystander.Id, true);
                    Assert.True(enabled == StoreWriteOutcome.Saved,
                        $"HARNESS: AlertDefinitionService.SetAlertEnabled could not enable the bystander alert ({enabled}).");
                    foreach (var server in servers)
                        answers.Set(server, () => Task.FromResult<object?>(DBNull.Value));
                    controlSet = "enabled alerts [" + bystander.Id + "], both seeded servers enabled";
                }

                await svc.EvaluateAllAsync();

                var control = KeyCensus(svc, perServer, alert.Id, servers);
                _out.WriteLine($"[{emptied}] AFTER the control cycle:\n" + control.Report);
                AssertReadable(control);
                var stillHeld = control.Held.Where(h => h.Value)
                    .Select(h => h.Key.Field + " still holds " + h.Key.Server).ToList();
                var stillOpen = servers
                    .Where(s => svc.ActiveAlerts.Any(a => a.AlertId == alert.Id && SameServer(a.ServerName, s))
                                || HistoryRows(history, alert.Id, s).Any(IsOpen))
                    .ToList();
                Assert.True(stillHeld.Count == 0 && stillOpen.Count == 0,
                    $"CONTROL FAILED [{emptied}]: with a NON-empty set that excludes the seeded keys ({controlSet}), "
                    + "the cycle left them in place."
                    + "\n   keys: " + (stillHeld.Count == 0 ? "none" : string.Join("; ", stillHeld))
                    + "\n   alert or history row still open: " + (stillOpen.Count == 0 ? "none" : string.Join("; ", stillOpen))
                    + "\n\nThis engine could not show a prune, so the survival asserted above is the silence of "
                    + "an instrument that never prunes, not evidence about the guards. WHAT TO CHECK: whether "
                    + "EvaluateAllAsync still calls PruneUnmonitoredAlertKeys and ReconcileEvaluationFailures "
                    + "past its configuration guards, and whether they cover each collection named above.\n"
                    + control.Report);
            }
            finally
            {
                // Leave the shared history database as this case found it.
                foreach (var server in servers.Append(outsider)) history.ResolveAlert(alert.Id, server);
                history.Dispose();
            }
        }

        // =========================================================================================
        // harness
        // =========================================================================================

        /// <summary>
        /// A one-second alert that MEASURES against a refused socket. Built on the shipped
        /// windows_power_plan definition and then re-pointed at connectivity_check, which is the
        /// same substitution SpecialAlertEscalationTests documents: the shipped registry_check needs
        /// a live registry read, connectivity_check measures exactly 1 against a refused socket, and
        /// the threshold travels WITH the measurement, so Warning is 0 and not the shipped 1.
        /// </summary>
        private static AlertDefinition MeasurableAlert()
        {
            var alert = Shipped("windows_power_plan");
            alert.Id = "alert_stamp_key_" + Guid.NewGuid().ToString("N");
            alert.QueryMode = "connectivity_check";
            alert.Thresholds = new AlertThresholds { Warning = 0 };
            alert.FrequencySeconds = FrequencySeconds;
            alert.Escalate = false;   // with DryRun on nothing dispatches anyway; this is belt and braces
            return alert;
        }

        private static AlertDefinition Shipped(string id)
        {
            var json = File.ReadAllText(Path.Combine(
                RawPassedScan.RepoRoot().FullName, "Config", "alert-definitions.json"));
            var file = System.Text.Json.JsonSerializer.Deserialize<AlertDefinitionsFile>(
                json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(file);
            return file!.Alerts.Single(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private string DefinitionsFileContaining(params AlertDefinition[] alerts)
        {
            var file = new AlertDefinitionsFile { Alerts = new List<AlertDefinition>(alerts) };
            var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + "-alert-definitions.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(file));
            return path;
        }

        private static (ServerConnection, AlertGlobalDefaults) Rig() =>
            (new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = ServerA,
                UseWindowsAuthentication = true,
                ConnectionTimeout = 2,
                IsEnabled = true,
            }, new AlertGlobalDefaults());

        private AlertEvaluationService BuildEngine(
            string definitionsFilePath,
            ServerConnectionManager? connections = null,
            AlertHistoryService? history = null,
            ServerCircuitBreakerService? breaker = null,
            AlertDefinitionService? definitions = null)
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);

            return new AlertEvaluationService(
                NullLogger<AlertEvaluationService>.Instance,
                definitions ?? new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, definitionsFilePath),
                history ?? new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                connections ?? new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                channels,
                new liveQueriesCacheStore(),
                new InlineOrchestrator(),
                breaker: breaker,
                // Never the installed Config/eval-failures.json.
                evalFailureStorePath: Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".json"));
        }

        /// <summary>A standard-path alert (no queryMode) that fires on any value above 0. Its query text is
        /// never sent: every test that uses it answers through StandardQueryOverrideForTests.</summary>
        private static AlertDefinition StandardAlert(int frequencySeconds) => new()
        {
            Id = "alert_stamp_standard_" + Guid.NewGuid().ToString("N"),
            Name = "Alert stamp standard-path probe",
            Query = "SELECT 1",
            QueryMode = "standard",
            Operator = "greater_than",
            Thresholds = new AlertThresholds { Warning = 0 },
            FrequencySeconds = frequencySeconds,
            AlwaysAlert = true,     // an operational window in the test tree must not suppress the cycle
            Escalate = false,
        };

        /// <summary>The definitions file for a test that runs a WHOLE cycle. A cycle also runs
        /// AlertHistoryService.AutoAcknowledge against the SHARED test history database, so its cutoff
        /// is pushed out of reach here and this test cannot acknowledge another test's rows.</summary>
        private string CycleDefinitionsFileContaining(params AlertDefinition[] alerts)
        {
            var file = new AlertDefinitionsFile
            {
                Alerts = new List<AlertDefinition>(alerts),
                GlobalDefaults = new AlertGlobalDefaults { AutoAcknowledgeHours = 24 * 365 * 50 },
            };
            var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + "-alert-definitions.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(file));
            return path;
        }

        /// <summary>A connection store in the temp directory, one connection per server, loaded by the
        /// REAL ServerConnectionManager so a removal goes through RemoveConnection as the product's does.</summary>
        private (ServerConnectionManager Manager, Dictionary<string, ServerConnection> ByServer) ConnectionsStore(params string[] servers)
        {
            var byServer = new Dictionary<string, ServerConnection>(StringComparer.OrdinalIgnoreCase);
            foreach (var server in servers)
                byServer[server] = new ServerConnection
                {
                    Id = Guid.NewGuid().ToString(),
                    ServerNames = server,
                    UseWindowsAuthentication = true,
                    ConnectionTimeout = 2,
                    IsEnabled = true,
                };
            var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + "-server-connections.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(byServer.Values.ToList()));
            var manager = new ServerConnectionManager(
                NullLogger<ServerConnectionManager>.Instance, seats: null, connectionsFilePath: path);
            return (manager, byServer);
        }

        private static void AssertEnabledServersAre(ServerConnectionManager connections, params string[] expected)
        {
            var enabled = connections.GetEnabledConnections().SelectMany(c => c.GetServerList()).ToList();
            var same = enabled.Count == expected.Length
                       && expected.All(e => enabled.Contains(e, StringComparer.OrdinalIgnoreCase));
            Assert.True(same,
                "HARNESS: the enabled set is [" + string.Join(" | ", enabled) + "] and this test expected ["
                + string.Join(" | ", expected) + "]. Everything this test says about pruning is relative to "
                + "that set. Check how ServerConnection.GetServerList splits and sanitises these names.");
        }

        /// <summary>
        /// Puts a key for (alert, server) into every per-(alert, server) collection the engine has, through
        /// the REAL standard path: one cycle whose query completes with a breaching value (state, notify
        /// stamp, hit queue, measurement clock and - with DryRun off and Escalate on - the escalation
        /// episode), then one whose query throws (the evaluation-failure record).
        /// </summary>
        private static async Task SeedEveryPerServerCollectionAsync(
            AlertEvaluationService svc, QueryAnswers answers, AlertDefinition alert, ServerConnection connection, string server)
        {
            answers.Set(server, () => Task.FromResult<object?>(5.0));
            await svc.ThrottledEvaluateAsync(alert, connection, server, new AlertGlobalDefaults(), CancellationToken.None);

            answers.Set(server, () => throw new TimeoutException("seeding an evaluation failure (test seam)"));
            await svc.ThrottledEvaluateAsync(alert, connection, server, new AlertGlobalDefaults(), CancellationToken.None);
        }

        /// <summary>Every collection the census classifies PerAlertServer, from its own enumerator.</summary>
        private static List<FieldInfo> PerServerCollections() =>
            AlertStampKeyCensusTests.KeyedCollections()
                .Where(c => c.Scope == AlertKeyScopeKind.PerAlertServer)
                .Select(c => c.Field)
                .ToList();

        private sealed record KeyCensusResult(
            Dictionary<(string Field, string Server), bool> Held, List<string> Unreadable, string Report);

        /// <summary>
        /// For each collection and each server: does the collection hold StateKey(alert, server)? The key
        /// is COMPOSED through the product's own StateKey and compared whole; no key is ever parsed. A
        /// collection that cannot be read is reported as unreadable and never as holding nothing.
        /// </summary>
        private static KeyCensusResult KeyCensus(
            AlertEvaluationService svc, IReadOnlyList<FieldInfo> collections, string alertId, params string[] servers)
        {
            var held = new Dictionary<(string Field, string Server), bool>();
            var unreadable = new List<string>();
            var report = new System.Text.StringBuilder();

            foreach (var field in collections)
            {
                System.Collections.IDictionary? dict;
                try
                {
                    dict = field.GetValue(svc) as System.Collections.IDictionary;
                }
                catch (Exception ex)
                {
                    unreadable.Add(field.Name + " - " + ex.GetType().Name + ": " + ex.Message);
                    report.AppendLine("   " + field.Name + ": *** COULD NOT READ ***");
                    continue;
                }
                if (dict == null)
                {
                    unreadable.Add(field.Name + " - not readable as IDictionary");
                    report.AppendLine("   " + field.Name + ": *** COULD NOT READ ***");
                    continue;
                }

                var keys = dict.Keys.Cast<object>().Select(k => k?.ToString() ?? "").ToList();
                var line = new List<string>();
                foreach (var server in servers)
                {
                    var key = AlertEvaluationService.StateKey(alertId, server);
                    var has = keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                    held[(field.Name, server)] = has;
                    line.Add(server + " " + (has ? "HELD" : "absent"));
                }
                report.AppendLine("   " + field.Name + ": " + string.Join(" | ", line));
            }

            return new KeyCensusResult(held, unreadable, report.ToString());
        }

        private static void AssertReadable(KeyCensusResult census) =>
            Assert.True(census.Unreadable.Count == 0,
                "COULD NOT READ some per-(alert, server) collections, so this test is silent about them "
                + "rather than clearing them:\n   " + string.Join("\n   ", census.Unreadable)
                + "\n\nWHAT TO CHECK: whether those fields are still dictionaries this reader can "
                + "enumerate.\n" + census.Report);

        private static void AssertEveryCollectionWasExercised(KeyCensusResult census)
        {
            var unexercised = census.Held.Where(h => !h.Value)
                .Select(h => h.Key.Field + " for " + h.Key.Server).ToList();
            Assert.True(unexercised.Count == 0,
                "SPECIMEN FAILED: after the seeding drive these per-(alert, server) collections hold no key, "
                + "so what they hold after the cycle would prove nothing:\n   "
                + string.Join("\n   ", unexercised)
                + "\n\nWHAT TO CHECK: whether a collection was added or re-scoped that "
                + "SeedEveryPerServerCollectionAsync does not reach, and whether PruneUnmonitoredAlertKeys "
                + "handles that collection.\n" + census.Report);
        }

        private static List<AlertHistoryRecord> HistoryRows(AlertHistoryService history, string alertId, string server) =>
            history.GetHistoryByAlert(alertId).Where(r => SameServer(r.ServerName, server)).ToList();

        private static bool IsOpen(AlertHistoryRecord row) =>
            row.Status == "Active" || row.Status == "Acknowledged";

        private static bool SameServer(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Makes every state of this alert look stale to the reaper: nothing re-fired it.</summary>
        private static void AgeLastTriggered(AlertEvaluationService svc, AlertDefinition alert)
        {
            foreach (var s in svc.ActiveAlerts.Where(s => s.AlertId == alert.Id))
                s.LastTriggered = DateTime.UtcNow.AddSeconds(-alert.FrequencySeconds * 20);
        }

        /// <summary>A REAL SqlException, not a constructed one: nothing listens on TCP port 1, so the open is
        /// refused - the same fault AlertBreakerLiveEndpointTests drives. The return is never reached.</summary>
        private static async Task<object?> RefusedSqlConnectionAsync()
        {
            using var refused = new Microsoft.Data.SqlClient.SqlConnection(
                "Data Source=127.0.0.1,1;Integrated Security=true;Connect Timeout=2;Encrypt=false;Pooling=false");
            await refused.OpenAsync();
            return null;
        }

        /// <summary>
        /// Answers the standard query per server through AlertEvaluationService.StandardQueryOverrideForTests
        /// with the RAW scalar ExecuteScalarAsync would return - a boxed double for a number, DBNull.Value
        /// or null for no value - or throws in its place, and counts how often each server was asked - which is how a test proves a server was attempted, or
        /// skipped, rather than assuming it. A server with no answer set throws, loudly.
        /// </summary>
        private sealed class QueryAnswers
        {
            private readonly ConcurrentDictionary<string, Func<Task<object?>>> _answers = new(StringComparer.OrdinalIgnoreCase);
            private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

            public QueryAnswers(AlertEvaluationService svc) =>
                svc.StandardQueryOverrideForTests = (alertDefinition, server) =>
                {
                    _calls.AddOrUpdate(server, 1, (key, count) => count + 1);
                    return _answers.TryGetValue(server, out var answer)
                        ? answer()
                        : throw new InvalidOperationException(
                            "the harness set no answer for server '" + server + "', so this test reached a server it did not expect");
                };

            public void Set(string server, Func<Task<object?>> answer) => _answers[server] = answer;

            public int Calls(string server) => _calls.TryGetValue(server, out var count) ? count : 0;
        }

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
