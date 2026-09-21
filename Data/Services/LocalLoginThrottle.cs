/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// What a caller is allowed to do next on <c>/auth/local</c>.
    /// </summary>
    public enum LoginAdmission
    {
        /// <summary>Verify the password.</summary>
        Allowed,

        /// <summary>This IP has failed too often and is in its lockout window.</summary>
        LockedOut,

        /// <summary>
        /// Too many verifications are already running. Distinct from <see cref="LockedOut"/> on
        /// purpose: it is a statement about the SERVER's load, not about this caller's attempts,
        /// and it must never be counted as a failed attempt (see <see cref="LocalLoginThrottle"/>).
        /// </summary>
        Busy,
    }

    /// <summary>
    /// Two limits in front of the Argon2id verify on <c>/auth/local</c>, for two different
    /// threats.
    ///
    /// <para><b>1. Per-IP attempt limiting.</b> Extends the shape already working in
    /// <see cref="AdminAuthService"/> — an attempt counter, a lockout timestamp, and the same
    /// <c>MaxAttempts</c> / <c>LockoutDuration</c> constants — rather than inventing a scheme
    /// beside it. It could not literally reuse that service: AdminAuthService keeps ONE counter
    /// for the whole singleton, which is right for a local desktop dialog with one operator in
    /// front of it and wrong for a network endpoint, where one global counter means any caller can
    /// lock out every caller. So the shape is cloned into a per-IP structure and the constants are
    /// kept, so the two surfaces answer the same way for the same number of failures.</para>
    ///
    /// <para><b>2. A concurrency cap.</b> A different threat, and the reason a lockout alone is not
    /// enough. <c>RbacService.ComputeArgon2</c> uses 19 MiB and 2 iterations (OWASP 2024), and
    /// <c>ValidateLocalLogin</c> runs a DUMMY hash on every miss so a wrong username costs the same
    /// as a wrong password — deliberate, and it means EVERY post pays 19 MiB whether or not the
    /// account exists. Unbounded parallel posts are therefore a memory-exhaustion vector that no
    /// per-IP counter addresses, because the first attempt from each of a thousand IPs is inside
    /// every allowance. <see cref="MaxConcurrentVerifications"/> bounds the peak at
    /// 4 × 19 MiB ≈ 76 MiB.</para>
    ///
    /// <para><b>Busy is not a failed attempt.</b> If a rejected-for-load post incremented the
    /// counter, an attacker could flood the endpoint and lock out every legitimate IP without ever
    /// guessing a password — the limiter would become the denial of service. The two outcomes are
    /// separate states for that reason.</para>
    ///
    /// <para>⚠ <b>SECOND LAYER, NOT THE BOUNDARY</b>, the same caveat ShellSurfaceRegistry carries.
    /// A non-loopback caller with no session is already refused by
    /// <c>InteractiveAppAdmission</c>. This narrows what an admitted caller can spend.</para>
    /// </summary>
    public sealed class LocalLoginThrottle : IDisposable
    {
        /// <summary>Failures from one IP before that IP is locked out. AdminAuthService's number.</summary>
        public const int MaxAttempts = 5;

        /// <summary>
        /// How long a locked-out IP stays locked out. AdminAuthService's value, kept so the two
        /// sign-in surfaces do not answer differently for the same number of failures.
        ///
        /// <para>Stated rather than implied: one minute after five failures allows roughly 7,200
        /// guesses per day per IP. Against Argon2id at 19 MiB that is not a practical offline-speed
        /// attack, and it is not a strong online limit either. Raising it is a policy change with a
        /// lockout cost to real operators, so it is left at the in-repo value and named here rather
        /// than quietly chosen.</para>
        /// </summary>
        public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Simultaneous Argon2id verifications. Four × 19 MiB ≈ 76 MiB peak from this endpoint.
        /// </summary>
        public const int MaxConcurrentVerifications = 4;

        /// <summary>
        /// How long a post waits for a slot before it is refused as <see cref="LoginAdmission.Busy"/>.
        /// Bounded on purpose: an unbounded queue in front of an expensive operation is the same
        /// exhaustion with an extra step, spent on connections instead of memory.
        /// </summary>
        public static readonly TimeSpan DefaultMaxQueueWait = TimeSpan.FromSeconds(5);

        /// <summary>This instance's queue wait. <see cref="DefaultMaxQueueWait"/> unless a test shortens it.</summary>
        internal TimeSpan MaxQueueWait { get; }

        /// <summary>An IP is forgotten once it has been quiet for this long.</summary>
        private static readonly TimeSpan EntryIdleLifetime = TimeSpan.FromMinutes(30);

        private sealed class Attempts
        {
            public int Failures;
            public DateTime LockoutUntilUtc = DateTime.MinValue;
            public DateTime LastSeenUtc = DateTime.UtcNow;
        }

        private readonly ConcurrentDictionary<string, Attempts> _byCaller = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _verifySlots = new(MaxConcurrentVerifications, MaxConcurrentVerifications);
        private readonly Func<DateTime> _utcNow;
        private int _sinceLastPrune;

        public LocalLoginThrottle() : this(() => DateTime.UtcNow, DefaultMaxQueueWait) { }

        /// <summary>
        /// Test seam: a clock the caller controls so a lockout can expire without waiting, and a
        /// shorter queue wait so the busy path can be exercised many times over. The wait is a
        /// duration, not a behaviour — shortening it changes how long a refusal takes to arrive,
        /// never whether one does.
        /// </summary>
        internal LocalLoginThrottle(Func<DateTime> utcNow, TimeSpan maxQueueWait)
        {
            _utcNow = utcNow;
            MaxQueueWait = maxQueueWait;
        }

        /// <summary>
        /// Normalises a caller to a limiter key. A null address (a request with no remote endpoint)
        /// gets its own bucket rather than an exemption: an unattributable caller is the one that
        /// most needs a limit, and letting it through was the shape of every fail-open defect in
        /// this tree.
        ///
        /// <para>⚠ <b>WHAT "CALLER" MEANS HERE, disclosed 2026-08-06 and NOT fixed.</b> The only
        /// input is the TRANSPORT peer address — <c>ctx.Connection.RemoteIpAddress</c>, the other
        /// end of the socket. Behind a reverse proxy, a load balancer or any NAT, that is the
        /// PROXY's address for every caller, so all of them share ONE bucket. Two effects, both
        /// real and both untested here because no such deployment has been probed:</para>
        /// <list type="bullet">
        /// <item>Five failures from anybody lock out everybody who arrives through that hop, which
        ///   is the global-counter behaviour this class was written to get away from.</item>
        /// <item>One attacker inside such a deployment cannot be distinguished from the population
        ///   sharing the address, so per-IP limiting stops being per-caller at all.</item>
        /// </list>
        /// <para>Reading a forwarded-for header instead is NOT a fix without a trusted-proxy
        /// allow-list: that header is caller-controlled, and honouring it unconditionally hands
        /// every attacker a fresh bucket per request, which is worse than one shared bucket. The
        /// decision is which proxies this install trusts, and that is configuration nobody has
        /// been asked for. Recorded here rather than guessed at.</para>
        /// </summary>
        public static string KeyFor(IPAddress? address) => address?.ToString() ?? "(unknown)";

        /// <summary>True while this caller is inside its lockout window.</summary>
        public bool IsLockedOut(string caller) =>
            _byCaller.TryGetValue(caller, out var a) && a.LockoutUntilUtc > _utcNow();

        /// <summary>Remaining lockout for this caller, or zero.</summary>
        public TimeSpan LockoutRemaining(string caller) =>
            _byCaller.TryGetValue(caller, out var a) && a.LockoutUntilUtc > _utcNow()
                ? a.LockoutUntilUtc - _utcNow()
                : TimeSpan.Zero;

        /// <summary>
        /// Asks for permission to run one verification, and reserves a slot when it is granted.
        ///
        /// <para>The caller MUST call <see cref="Release"/> exactly once for every
        /// <see cref="LoginAdmission.Allowed"/> — a slot that is taken and never given back
        /// permanently shrinks the endpoint's capacity, which is the failure this cap exists to
        /// prevent, arriving by the other door.</para>
        /// </summary>
        public async Task<LoginAdmission> TryBeginVerificationAsync(string caller, CancellationToken ct = default)
        {
            PruneOccasionally();

            // Checked BEFORE a slot is taken: a locked-out caller must not be able to occupy the
            // cap it is not allowed to use.
            if (IsLockedOut(caller)) return LoginAdmission.LockedOut;

            if (!await _verifySlots.WaitAsync(MaxQueueWait, ct).ConfigureAwait(false))
                return LoginAdmission.Busy;

            // The window between the check above and the slot is real: a concurrent post from the
            // same caller could have tripped the lockout while this one waited. Re-check and hand
            // the slot straight back rather than verify for a caller that is now locked out.
            if (IsLockedOut(caller))
            {
                _verifySlots.Release();
                return LoginAdmission.LockedOut;
            }

            return LoginAdmission.Allowed;
        }

        /// <summary>Returns a slot taken by <see cref="TryBeginVerificationAsync"/>.</summary>
        public void Release() => _verifySlots.Release();

        /// <summary>
        /// Records one verification that produced no session. Trips the lockout at
        /// <see cref="MaxAttempts"/>.
        /// </summary>
        public void RecordFailure(string caller)
        {
            var now = _utcNow();
            var entry = _byCaller.GetOrAdd(caller, _ => new Attempts());
            lock (entry)
            {
                entry.LastSeenUtc = now;
                entry.Failures++;
                if (entry.Failures >= MaxAttempts)
                {
                    entry.LockoutUntilUtc = now + LockoutDuration;
                    entry.Failures = 0;
                }
            }
        }

        /// <summary>Clears this caller's history after a verification that produced a session.</summary>
        public void RecordSuccess(string caller)
        {
            if (!_byCaller.TryGetValue(caller, out var entry)) return;
            lock (entry)
            {
                entry.Failures = 0;
                entry.LockoutUntilUtc = DateTime.MinValue;
                entry.LastSeenUtc = _utcNow();
            }
        }

        /// <summary>
        /// Drops callers that have been quiet longer than <see cref="EntryIdleLifetime"/>. Without
        /// this the dictionary is itself an unbounded allocation an attacker controls — one entry
        /// per source address, forever.
        /// </summary>
        private void PruneOccasionally()
        {
            if (Interlocked.Increment(ref _sinceLastPrune) % 256 != 0) return;

            var cutoff = _utcNow() - EntryIdleLifetime;
            foreach (var (key, entry) in _byCaller)
            {
                bool stale;
                lock (entry) stale = entry.LastSeenUtc < cutoff && entry.LockoutUntilUtc < _utcNow();
                if (stale) _byCaller.TryRemove(key, out _);
            }
        }

        public void Dispose() => _verifySlots.Dispose();
    }
}
