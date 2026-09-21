/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Defect C (2026-08-01): the shared circuit breaker was reset by a FALSE success signal. The
    /// alert loop called <c>RecordSuccess</c> whenever the orchestrator's <c>result.Success</c> was
    /// true — but that flag only says the work delegate did not throw, and no evaluation path ever
    /// throws (each catches its own SqlException, logs, and returns so AlertsNoc can render Unknown).
    /// Polling a DEAD server therefore reported success and closed the circuit on the next tick. The
    /// live log shows 143 "circuit OPENED" and 143 "circuit CLOSED", perfectly paired:
    /// ConnectionHealthService opened it on real failures, the alert loop closed it again immediately.
    /// </summary>
    public class AlertBreakerOutcomeTests
    {
        private static ServerCircuitBreakerService NewBreaker()
            => new(NullLogger<ServerCircuitBreakerService>.Instance, audit: null);

        // Invariant B (2026-09-19): a failure now travels WITH its cause, and the probe has no public
        // setter. These tests are about the breaker's wiring, not about classification, so an
        // Unreachable probe is built from a real no-answer (a transport error at severity 20). The
        // classification itself is ServerAnswerClassifierTests.
        private static readonly System.Exception NoAnswer = ServerAnswerClassifierTests.MakeSqlException((11001, 20));

        private static AlertEvaluationService.ServerReachabilityProbe Probe(AlertEvaluationService.ServerReachability wanted)
        {
            var probe = new AlertEvaluationService.ServerReachabilityProbe();
            if (wanted == AlertEvaluationService.ServerReachability.Reached) probe.MarkReached();
            else if (wanted == AlertEvaluationService.ServerReachability.Unreachable) probe.MarkFailed(NoAnswer);
            Assert.Equal(wanted, probe.Reachability);
            return probe;
        }

        // ── the required case: internal failures must OPEN the breaker and KEEP it open ──

        [Fact]
        public void EvaluationsThatFailInternally_OpenTheBreakerAndKeepItOpen()
        {
            var breaker = NewBreaker();
            const string server = "dead-server";

            // Six cycles in which the orchestrator succeeded (the delegate did not throw) but the
            // evaluation itself could not reach SQL Server. Pre-fix, every one of these called
            // RecordSuccess and reset ConsecutiveFailures to 0, so the circuit never stayed open.
            for (int i = 0; i < 6; i++)
            {
                AlertEvaluationService.ApplyBreakerOutcome(
                    breaker, server, Probe(AlertEvaluationService.ServerReachability.Unreachable));

                if (i >= 2)
                {
                    Assert.False(breaker.ShouldAttempt(server),
                        $"Circuit should be open after {i + 1} internally-failed evaluations.");
                }
            }

            Assert.False(breaker.ShouldAttempt(server),
                "The circuit must STAY open across consecutive failing cycles, not be reset each tick.");
        }

        [Fact]
        public void PreFixBehaviour_RecordingSuccessOnAFailedEvaluation_ReopensTheCircuitEveryTick()
        {
            // Characterises the defect: this is exactly what the wrappers used to do, and it is why
            // the OPENED/CLOSED counts in the live log were identical.
            var breaker = NewBreaker();
            const string server = "dead-server";

            breaker.RecordFailure(server, NoAnswer);
            breaker.RecordFailure(server, NoAnswer);
            breaker.RecordFailure(server, NoAnswer);
            Assert.False(breaker.ShouldAttempt(server));

            breaker.RecordSuccess(server); // the false signal
            Assert.True(breaker.ShouldAttempt(server));
        }

        // ── the other subsystem's failures must survive an alert cycle ──

        [Fact]
        public void UndeterminedOutcome_DoesNotResetAnotherSubsystemsOpenCircuit()
        {
            var breaker = NewBreaker();
            const string server = "dead-server";

            // ConnectionHealthService — which sees the real connection failures — opens the circuit.
            breaker.RecordFailure(server, NoAnswer);
            breaker.RecordFailure(server, NoAnswer);
            breaker.RecordFailure(server, NoAnswer);
            Assert.False(breaker.ShouldAttempt(server));

            // An alert cycle that learned nothing about the server (skipped, or a special-mode
            // handler that converts unreachability into a value) must leave that verdict alone.
            for (int i = 0; i < 5; i++)
            {
                AlertEvaluationService.ApplyBreakerOutcome(
                    breaker, server, Probe(AlertEvaluationService.ServerReachability.Undetermined));
            }

            Assert.False(breaker.ShouldAttempt(server),
                "An Undetermined outcome must not close a circuit another subsystem opened.");
        }

        // ── and the fix must not make the breaker unrecoverable ──

        [Fact]
        public void ReachedServer_StillClosesTheCircuit()
        {
            var breaker = NewBreaker();
            const string server = "recovering-server";

            AlertEvaluationService.ApplyBreakerOutcome(breaker, server, Probe(AlertEvaluationService.ServerReachability.Unreachable));
            AlertEvaluationService.ApplyBreakerOutcome(breaker, server, Probe(AlertEvaluationService.ServerReachability.Unreachable));
            AlertEvaluationService.ApplyBreakerOutcome(breaker, server, Probe(AlertEvaluationService.ServerReachability.Unreachable));
            Assert.False(breaker.ShouldAttempt(server));

            // A query that actually completed is the one signal entitled to close the circuit.
            AlertEvaluationService.ApplyBreakerOutcome(breaker, server, Probe(AlertEvaluationService.ServerReachability.Reached));

            Assert.True(breaker.ShouldAttempt(server),
                "A genuinely reachable server must still close the circuit — the fix must not strand it open.");
        }

        [Fact]
        public void ApplyBreakerOutcome_WithNoBreakerConfigured_IsANoOp()
        {
            // The breaker is an optional dependency in minimal DI/test graphs.
            AlertEvaluationService.ApplyBreakerOutcome(null, "srv", Probe(AlertEvaluationService.ServerReachability.Unreachable));
        }

        [Fact]
        public void ReachabilityProbe_DefaultsToUndetermined()
        {
            // The default matters: an evaluation that returns without setting it must leave the
            // breaker untouched, never report success.
            var probe = new AlertEvaluationService.ServerReachabilityProbe();
            Assert.Equal(AlertEvaluationService.ServerReachability.Undetermined, probe.Reachability);
        }

        [Fact]
        public void OneServersFailures_DoNotSuppressAnother()
        {
            var breaker = NewBreaker();

            for (int i = 0; i < 4; i++)
            {
                AlertEvaluationService.ApplyBreakerOutcome(
                    breaker, "dead", Probe(AlertEvaluationService.ServerReachability.Unreachable));
            }

            Assert.False(breaker.ShouldAttempt("dead"));
            Assert.True(breaker.ShouldAttempt("healthy"),
                "Back-off is per-server; a dead instance must not delay alerting on the healthy ones.");
        }
    }
}
