/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The two limits in front of the Argon2id verify on /auth/local. Nothing stood there: no
    /// attempt limit, and no bound on how many 19 MiB verifications could run at once.
    /// </summary>
    public class LocalLoginThrottleTests
    {
        private static DateTime _clock = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
        /// <summary>
        /// A controlled clock, and a queue wait shortened from five seconds so the busy path can be
        /// exercised repeatedly. The duration is not the behaviour under test; that a refusal
        /// ARRIVES is, and the wait's real length is asserted separately below against the shipped
        /// default.
        /// </summary>
        private static readonly TimeSpan TestQueueWait = TimeSpan.FromMilliseconds(200);
        private static LocalLoginThrottle NewThrottle() => new(() => _clock, TestQueueWait);

        private static async Task<LoginAdmission> AttemptAsync(LocalLoginThrottle t, string caller, bool succeeds)
        {
            var admission = await t.TryBeginVerificationAsync(caller);
            if (admission != LoginAdmission.Allowed) return admission;

            t.Release();
            if (succeeds) t.RecordSuccess(caller); else t.RecordFailure(caller);
            return admission;
        }

        // ── Per-IP attempt limiting ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_first_attempt_from_an_unseen_caller_is_allowed()
        {
            using var t = NewThrottle();
            Assert.Equal(LoginAdmission.Allowed, await t.TryBeginVerificationAsync("10.0.0.1"));
            t.Release();
        }

        [Fact]
        public async Task Five_failures_lock_the_caller_out_and_the_sixth_never_reaches_the_verify()
        {
            _clock = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
            using var t = NewThrottle();

            for (var i = 0; i < LocalLoginThrottle.MaxAttempts; i++)
                Assert.Equal(LoginAdmission.Allowed, await AttemptAsync(t, "10.0.0.1", succeeds: false));

            Assert.True(t.IsLockedOut("10.0.0.1"));
            Assert.Equal(LoginAdmission.LockedOut, await t.TryBeginVerificationAsync("10.0.0.1"));
        }

        [Fact]
        public async Task A_lockout_is_PER_CALLER_not_global()
        {
            // This is the whole reason AdminAuthService could not be reused as-is: it keeps one
            // counter for the singleton, which on a network endpoint means any caller can lock out
            // every caller.
            _clock = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
            using var t = NewThrottle();

            for (var i = 0; i < LocalLoginThrottle.MaxAttempts; i++)
                await AttemptAsync(t, "10.0.0.1", succeeds: false);

            Assert.True(t.IsLockedOut("10.0.0.1"));
            Assert.False(t.IsLockedOut("10.0.0.2"));
            Assert.Equal(LoginAdmission.Allowed, await t.TryBeginVerificationAsync("10.0.0.2"));
            t.Release();
        }

        [Fact]
        public async Task The_lockout_expires_after_the_configured_duration()
        {
            _clock = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
            using var t = NewThrottle();

            for (var i = 0; i < LocalLoginThrottle.MaxAttempts; i++)
                await AttemptAsync(t, "10.0.0.1", succeeds: false);
            Assert.True(t.IsLockedOut("10.0.0.1"));

            _clock += LocalLoginThrottle.LockoutDuration + TimeSpan.FromSeconds(1);

            Assert.False(t.IsLockedOut("10.0.0.1"));
            Assert.Equal(TimeSpan.Zero, t.LockoutRemaining("10.0.0.1"));
            Assert.Equal(LoginAdmission.Allowed, await t.TryBeginVerificationAsync("10.0.0.1"));
            t.Release();
        }

        [Fact]
        public async Task A_success_clears_the_history_so_a_fat_fingered_operator_is_not_carried_forward()
        {
            _clock = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
            using var t = NewThrottle();

            for (var i = 0; i < LocalLoginThrottle.MaxAttempts - 1; i++)
                await AttemptAsync(t, "10.0.0.1", succeeds: false);
            await AttemptAsync(t, "10.0.0.1", succeeds: true);

            // Four more failures must not trip the lockout, because the count restarted.
            for (var i = 0; i < LocalLoginThrottle.MaxAttempts - 1; i++)
                await AttemptAsync(t, "10.0.0.1", succeeds: false);

            Assert.False(t.IsLockedOut("10.0.0.1"));
        }

        [Fact]
        public void An_unattributable_caller_gets_a_bucket_rather_than_an_exemption()
        {
            // Fail-open on a missing input is the shape every fail-open defect in this tree had.
            Assert.Equal("(unknown)", LocalLoginThrottle.KeyFor(null));
            Assert.Equal("10.0.0.1", LocalLoginThrottle.KeyFor(IPAddress.Parse("10.0.0.1")));
            Assert.Equal("::1", LocalLoginThrottle.KeyFor(IPAddress.IPv6Loopback));
        }

        [Fact]
        public async Task Loopback_gets_no_exemption_either()
        {
            // The admission boundary trusts loopback. This limiter deliberately does not: a local
            // process guessing passwords is exactly as expensive as a remote one.
            _clock = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
            using var t = NewThrottle();

            var caller = LocalLoginThrottle.KeyFor(IPAddress.Loopback);
            for (var i = 0; i < LocalLoginThrottle.MaxAttempts; i++)
                await AttemptAsync(t, caller, succeeds: false);

            Assert.True(t.IsLockedOut(caller));
        }

        // ── Concurrency cap ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Only_the_configured_number_of_verifications_run_at_once()
        {
            using var t = NewThrottle();
            var held = new List<LoginAdmission>();

            for (var i = 0; i < LocalLoginThrottle.MaxConcurrentVerifications; i++)
                held.Add(await t.TryBeginVerificationAsync("10.0.0." + i));

            Assert.All(held, a => Assert.Equal(LoginAdmission.Allowed, a));

            // The next one waits, then is refused. Uses a distinct caller so this cannot be
            // mistaken for the per-IP limit doing the work.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var overflow = await t.TryBeginVerificationAsync("10.0.0.250");
            sw.Stop();

            Assert.Equal(LoginAdmission.Busy, overflow);
            Assert.True(sw.Elapsed >= TestQueueWait - TimeSpan.FromMilliseconds(80),
                $"it must WAIT for a slot before refusing, not refuse instantly (waited {sw.Elapsed}).");

            for (var i = 0; i < held.Count; i++) t.Release();
        }

        [Fact]
        public void The_shipped_queue_wait_is_bounded_and_not_zero()
        {
            // The tests above run with a shortened wait, so the real one is asserted here rather
            // than left unexamined. Unbounded would be the same exhaustion spent on connections;
            // zero would refuse a caller who only needed to wait a moment.
            Assert.True(LocalLoginThrottle.DefaultMaxQueueWait > TimeSpan.Zero);
            Assert.True(LocalLoginThrottle.DefaultMaxQueueWait <= TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task A_released_slot_is_reusable()
        {
            // A slot taken and never returned shrinks the endpoint's capacity for the life of the
            // process — the exhaustion this cap exists to prevent, arriving by the other door.
            using var t = NewThrottle();

            for (var round = 0; round < 3; round++)
            {
                for (var i = 0; i < LocalLoginThrottle.MaxConcurrentVerifications; i++)
                    Assert.Equal(LoginAdmission.Allowed, await t.TryBeginVerificationAsync("10.0.1." + i));
                for (var i = 0; i < LocalLoginThrottle.MaxConcurrentVerifications; i++)
                    t.Release();
            }
        }

        [Fact]
        public async Task BUSY_IS_NOT_A_FAILED_ATTEMPT()
        {
            // The load-bearing one. If a flood counted as failures, an attacker could lock out
            // every legitimate caller without guessing one password — the limiter would become the
            // denial of service it was added to prevent.
            _clock = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
            using var t = NewThrottle();

            for (var i = 0; i < LocalLoginThrottle.MaxConcurrentVerifications; i++)
                Assert.Equal(LoginAdmission.Allowed, await t.TryBeginVerificationAsync("10.0.2." + i));

            for (var i = 0; i < LocalLoginThrottle.MaxAttempts * 2; i++)
                Assert.Equal(LoginAdmission.Busy, await t.TryBeginVerificationAsync("10.0.0.9"));

            Assert.False(t.IsLockedOut("10.0.0.9"));

            for (var i = 0; i < LocalLoginThrottle.MaxConcurrentVerifications; i++) t.Release();
            Assert.Equal(LoginAdmission.Allowed, await t.TryBeginVerificationAsync("10.0.0.9"));
            t.Release();
        }

        [Fact]
        public async Task A_locked_out_caller_does_not_occupy_a_slot_it_may_not_use()
        {
            _clock = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
            using var t = NewThrottle();

            for (var i = 0; i < LocalLoginThrottle.MaxAttempts; i++)
                await AttemptAsync(t, "10.0.0.1", succeeds: false);

            // Refused without consuming capacity: all slots are still available afterwards.
            Assert.Equal(LoginAdmission.LockedOut, await t.TryBeginVerificationAsync("10.0.0.1"));

            for (var i = 0; i < LocalLoginThrottle.MaxConcurrentVerifications; i++)
                Assert.Equal(LoginAdmission.Allowed, await t.TryBeginVerificationAsync("10.0.3." + i));
            for (var i = 0; i < LocalLoginThrottle.MaxConcurrentVerifications; i++) t.Release();
        }

        [Fact]
        public async Task Concurrent_failures_from_one_caller_still_reach_the_lockout()
        {
            // The counter is incremented under a per-entry lock; a race that lost increments would
            // leave the lockout permanently one attempt away.
            _clock = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
            using var t = NewThrottle();

            await Task.WhenAll(Enumerable.Range(0, LocalLoginThrottle.MaxAttempts)
                .Select(_ => Task.Run(() => t.RecordFailure("10.0.0.7"))));

            Assert.True(t.IsLockedOut("10.0.0.7"));
        }
    }
}
