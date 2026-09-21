/* In the name of God, the Merciful, the Compassionate */

// INVARIANT B (lane alert-correctness, 2026-09-19): "the server refused me" / "the server answered
// with an error" is not "the server did not answer". Only a genuine no-answer counts toward opening
// the circuit breaker or marks a server Unreachable. ServerAnswerClassifier is the ONE place that
// decides; ServerCircuitBreakerService.RecordFailure(serverName, cause) and
// AlertEvaluationService.ServerReachabilityProbe.MarkFailed(cause) both ask it.
//
// The exceptions here are REAL Microsoft.Data.SqlClient.SqlException instances built through
// SqlClient's own internal factory, SqlException.CreateException(SqlErrorCollection, string), the
// same path the driver uses, with the numbers and severities MEASURED on this box on 2026-09-19
// against .\NEW2022 and .\OLD2017. The live twin of this class, ServerAnswerClassifierLiveTests,
// takes the same exceptions from the two local servers and re-measures them on every armed run.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class ServerAnswerClassifierTests
    {
        /// <summary>Builds a real SqlException carrying every (number, severity) given, in order.</summary>
        internal static SqlException MakeSqlException(params (int Number, byte Severity)[] errors)
        {
            var asm = typeof(SqlException).Assembly;
            var collectionType = asm.GetType("Microsoft.Data.SqlClient.SqlErrorCollection")
                ?? throw new InvalidOperationException("SqlErrorCollection not found: SqlClient internal shape changed.");
            var errorType = asm.GetType("Microsoft.Data.SqlClient.SqlError")
                ?? throw new InvalidOperationException("SqlError not found: SqlClient internal shape changed.");
            var collection = Activator.CreateInstance(collectionType, nonPublic: true)
                ?? throw new InvalidOperationException("Could not construct SqlErrorCollection.");
            var add = collectionType.GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("SqlErrorCollection.Add not found.");
            var ctor = errorType.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception) },
                null)
                ?? throw new InvalidOperationException("SqlError(int,byte,byte,string,string,string,int,Exception) not found.");

            foreach (var (number, severity) in errors)
            {
                // (infoNumber, errorState, errorClass, server, message, procedure, lineNumber, exception)
                var error = ctor.Invoke(new object?[] { number, (byte)1, severity, "ac-l1-test", $"error {number}", "", 1, null });
                add.Invoke(collection, new[] { error });
            }

            var create = typeof(SqlException).GetMethod("CreateException",
                BindingFlags.NonPublic | BindingFlags.Static, null, new[] { collectionType, typeof(string) }, null)
                ?? throw new InvalidOperationException("SqlException.CreateException(SqlErrorCollection,string) not found.");
            return (SqlException)create.Invoke(null, new[] { collection, (object)"" })!;
        }

        private static ServerCircuitBreakerService NewBreaker()
            => new(NullLogger<ServerCircuitBreakerService>.Instance, audit: null);

        // ── The rule, entry by entry ────────────────────────────────────────────

        public static IEnumerable<object[]> Answered() => new[]
        {
            new object[] { "VIEW SERVER STATE denial: 300 then 297 in ONE exception (measured)", new[] { 300, 14, 297, 16 } },
            new object[] { "object permission 229 (measured)", new[] { 229, 14 } },
            new object[] { "297 alone, index_fragmentation (measured)", new[] { 297, 16 } },
            new object[] { "THROW 50001 (measured)", new[] { 50001, 16 } },
            new object[] { "the first user-defined number", new[] { 50000, 16 } },
            new object[] { "two refusals in one batch", new[] { 229, 14, 229, 14 } },
        };

        public static IEnumerable<object[]> DidNotAnswer() => new[]
        {
            new object[] { "COMMAND TIMEOUT -2, severity 11 (measured): a hung server must stay backed off", new[] { -2, 11 } },
            new object[] { "unresolvable name 11001, severity 20 (measured)", new[] { 11001, 20 } },
            new object[] { "dead port 258, severity 20 (measured, connect timeout 5)", new[] { 258, 20 } },
            new object[] { "dead port 1225, severity 20 (measured, connect timeout 15)", new[] { 1225, 20 } },
            new object[] { "login refused 18456 (ruled: stays a failure)", new[] { 18456, 14 } },
            new object[] { "database unavailable at login 4060 then 18456", new[] { 4060, 11, 18456, 14 } },
            new object[] { "a refusal mixed with a login failure: EVERY entry must be answered", new[] { 300, 14, 297, 16, 18456, 14 } },
            new object[] { "one below the user-defined range", new[] { 49999, 16 } },
            new object[] { "a user-defined error raised at a fatal severity", new[] { 50001, 20 } },
            new object[] { "a permission number at a fatal severity", new[] { 229, 20 } },
            new object[] { "an informational line with the refusal (not proved to occur; kept strict)", new[] { 0, 0, 229, 14 } },
        };

        private static (int, byte)[] Pairs(int[] flat)
        {
            var list = new List<(int, byte)>();
            for (var i = 0; i < flat.Length; i += 2) list.Add((flat[i], (byte)flat[i + 1]));
            return list.ToArray();
        }

        [Theory]
        [MemberData(nameof(Answered))]
        public void A_server_that_refused_or_answered_with_an_error_answered(string why, int[] flat)
        {
            var ex = MakeSqlException(Pairs(flat));
            Assert.True(ServerAnswerClassifier.ServerAnswered(ex), why);
        }

        [Theory]
        [MemberData(nameof(DidNotAnswer))]
        public void Anything_else_did_not_answer(string why, int[] flat)
        {
            var ex = MakeSqlException(Pairs(flat));
            Assert.False(ServerAnswerClassifier.ServerAnswered(ex), why);
        }

        [Fact]
        public void The_first_entry_alone_never_decides()
        {
            // .Number reads the FIRST entry. A rule keyed on it would call this exception answered.
            var ex = MakeSqlException((300, 14), (18456, 14));
            Assert.Equal(300, ex.Number);
            Assert.False(ServerAnswerClassifier.ServerAnswered(ex));
        }

        [Fact]
        public void A_non_sql_cause_is_not_proof_the_server_answered()
        {
            Assert.False(ServerAnswerClassifier.ServerAnswered(null));
            Assert.False(ServerAnswerClassifier.ServerAnswered(new TimeoutException("slot")));
            Assert.False(ServerAnswerClassifier.ServerAnswered(new OperationCanceledException("CancelAfter on a hung login")));
            Assert.False(ServerAnswerClassifier.ServerAnswered(new InvalidOperationException("ours")));
            // A SqlException wrapped by someone else is not unwrapped: the answered set is not widened on a guess.
            Assert.False(ServerAnswerClassifier.ServerAnswered(new InvalidOperationException("wrapped", MakeSqlException((229, 14)))));
        }

        // ── The breaker asks the classifier ─────────────────────────────────────

        [Fact]
        public void The_breaker_never_opens_on_refusals_from_a_healthy_server()
        {
            var breaker = NewBreaker();
            const string server = "healthy-but-refusing";

            for (var i = 0; i < 12; i++)
            {
                breaker.RecordFailure(server, MakeSqlException((300, 14), (297, 16)));
                Assert.True(breaker.ShouldAttempt(server), $"refusal {i + 1}: a server that answered was backed off");
            }
        }

        [Fact]
        public void The_breaker_still_opens_on_three_command_timeouts_and_on_three_dead_transports()
        {
            var timeouts = NewBreaker();
            for (var i = 0; i < 3; i++) timeouts.RecordFailure("hung", MakeSqlException((-2, 11)));
            Assert.False(timeouts.ShouldAttempt("hung"), "a hung server must still be backed off");

            var dead = NewBreaker();
            for (var i = 0; i < 3; i++) dead.RecordFailure("dead", MakeSqlException((11001, 20)));
            Assert.False(dead.ShouldAttempt("dead"), "a server that is really down must still be backed off");

            var nonSql = NewBreaker();
            for (var i = 0; i < 3; i++) nonSql.RecordFailure("cancelled", new OperationCanceledException("a hung login under CancelAfter"));
            Assert.False(nonSql.ShouldAttempt("cancelled"), "a non-SQL cause reaching the breaker keeps today's meaning: a failure");
        }

        [Fact]
        public void A_refusal_between_failures_resets_the_count_because_the_server_answered()
        {
            var breaker = NewBreaker();
            const string server = "flapping";

            breaker.RecordFailure(server, MakeSqlException((11001, 20)));
            breaker.RecordFailure(server, MakeSqlException((11001, 20)));
            breaker.RecordFailure(server, MakeSqlException((229, 14)));   // answered: RecordSuccess
            breaker.RecordFailure(server, MakeSqlException((11001, 20)));
            breaker.RecordFailure(server, MakeSqlException((11001, 20)));
            Assert.True(breaker.ShouldAttempt(server), "only two consecutive no-answers since the server last answered");

            breaker.RecordFailure(server, MakeSqlException((11001, 20)));
            Assert.False(breaker.ShouldAttempt(server));
        }

        // ── The reachability probe asks the classifier ──────────────────────────

        [Fact]
        public void The_probe_maps_a_refusal_to_Reached_and_a_no_answer_to_Unreachable_and_keeps_the_cause()
        {
            var refused = new AlertEvaluationService.ServerReachabilityProbe();
            var refusal = MakeSqlException((300, 14), (297, 16));
            refused.MarkFailed(refusal);
            Assert.Equal(AlertEvaluationService.ServerReachability.Reached, refused.Reachability);
            Assert.Same(refusal, refused.Cause);

            var hung = new AlertEvaluationService.ServerReachabilityProbe();
            var timeout = MakeSqlException((-2, 11));
            hung.MarkFailed(timeout);
            Assert.Equal(AlertEvaluationService.ServerReachability.Unreachable, hung.Reachability);
            Assert.Same(timeout, hung.Cause);

            var reached = new AlertEvaluationService.ServerReachabilityProbe();
            reached.MarkReached();
            Assert.Equal(AlertEvaluationService.ServerReachability.Reached, reached.Reachability);
            Assert.Null(reached.Cause);
        }

        [Fact]
        public void ApplyBreakerOutcome_hands_the_carried_cause_to_the_breaker()
        {
            var breaker = NewBreaker();

            for (var i = 0; i < 6; i++)
            {
                var probe = new AlertEvaluationService.ServerReachabilityProbe();
                probe.MarkFailed(MakeSqlException((229, 14)));
                AlertEvaluationService.ApplyBreakerOutcome(breaker, "refusing", probe);
            }
            Assert.True(breaker.ShouldAttempt("refusing"));

            for (var i = 0; i < 3; i++)
            {
                var probe = new AlertEvaluationService.ServerReachabilityProbe();
                probe.MarkFailed(MakeSqlException((1225, 20)));
                AlertEvaluationService.ApplyBreakerOutcome(breaker, "dead", probe);
            }
            Assert.False(breaker.ShouldAttempt("dead"));
        }
    }
}
