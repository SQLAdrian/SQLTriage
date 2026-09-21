/* In the name of God, the Merciful, the Compassionate */

using System;
using SQLTriage.Data.Services;

namespace SQLTriage.Data
{
    /// <summary>
    /// What a caller is entitled to do to the process-wide current server.
    /// </summary>
    public enum ConnectionRetargetKind
    {
        /// <summary>
        /// The default, and it is the default ON PURPOSE. <c>default(ConnectionRetargetGrant)</c>
        /// is a refusal, so a call site that forgets to think — or a struct that arrives
        /// zero-initialised from anywhere at all — cannot move the connection.
        /// </summary>
        Refused = 0,

        /// <summary>The caller holds the permission named in <see cref="ConnectionRetargetGrant.Permission"/>.</summary>
        Authorised = 1,

        /// <summary>
        /// Establish a connection where the process has none. Accepted only while no server is
        /// selected; it can never move the target from one server to another. Enforced by
        /// <see cref="ServerConnectionManager.SetCurrentServer"/>, not trusted from the caller.
        /// <see cref="GlobalInstanceSelector.SetSelectedInstance"/> refuses it outright: that edge
        /// has no bootstrap, so there is nothing for an establish to mean there.
        /// </summary>
        EstablishOnly = 2,
    }

    /// <summary>
    /// The permission decision that BOTH process-wide connection edges demand, expressed in the
    /// type system.
    ///
    /// <para><b>Both, and the plural is the correction this header exists to carry.</b> It first
    /// said "THE CHOKEPOINT for the process-wide connection edge" while guarding exactly one of
    /// two. Every query that asks the factory for this process's AMBIENT target resolves it in
    /// <c>SqlServerConnectionFactory.GetCurrentConnectionString</c>, and that function reads
    /// <see cref="GlobalInstanceSelector"/><c>.SelectedInstance</c> FIRST, falling through to
    /// <c>ServerConnectionManager.CurrentServer</c> only when it is empty — so the edge closed
    /// first was the weaker of the two at the point of use, and the sentence claiming otherwise
    /// was defeated by a page (<c>Pages/Sessions.razor</c>) with two ungated writes of the
    /// stronger one. <see cref="GlobalInstanceSelector.SetSelectedInstance"/> now demands this
    /// same grant. The rule the two share: no code moves this process's SQL target without naming
    /// a decision.</para>
    ///
    /// <para><b>Why a parameter and not a guard.</b> This edge has been "closed" three times by
    /// gating the call sites that were visible at the time, and each round shipped another ungated
    /// one — most recently a fifth write inside <c>DiscoverAndUpdateInstancesAsync</c>, on the very
    /// init path the guarding comment named, invisible to a census that only read the body of the
    /// method it believed was the chokepoint. Enumerating call sites is the shape that keeps
    /// failing. <see cref="IServerConnectionManager.SetCurrentServer"/> and
    /// <see cref="GlobalInstanceSelector.SetSelectedInstance"/> both take a grant, so a call site
    /// added tomorrow to either does not compile until it says who is asking, and the only way to
    /// say "authorised" is to hand over an <see cref="AppUserState"/> and let it answer.</para>
    ///
    /// <para><b>What this does NOT claim.</b> It is not a claim that no code can ever move the
    /// connection: a caller holding an <see cref="AppUserState"/> that answers yes gets a real
    /// grant, which is the point. It is a claim that no code can move it <i>without naming a
    /// decision</i>, and that there are exactly two decisions available — ask this caller's own
    /// authorization state, or ask to ESTABLISH, which the manager itself refuses to let move an
    /// already-selected server. Neither is a boolean somebody typed.</para>
    ///
    /// <para>Nor is it a claim that the CATEGORY is now exhausted. The category is "process-wide
    /// state that decides which SQL Server this process's queries run against", and the way to
    /// enumerate it is to read <c>SqlServerConnectionFactory.GetCurrentConnectionString</c> — the
    /// resolver for every query that asks for the ambient target — and list what it consults. Today
    /// that is two things and both are here. A third one added to that function is a third edge, and
    /// neither this type nor any census in the tree will notice on its own.</para>
    ///
    /// <para>⚠ SCOPE, measured 2026-08-07 rather than assumed: this is NOT every query in the
    /// process. There are ~95 <c>new SqlConnection(...)</c> sites outside the tests, and many take a
    /// connection string from their caller or compose one inline (see
    /// <c>DatabaseAvailabilityService</c>, which builds <c>Server={serverName};…</c> itself). Those
    /// never consult this function and this grant does not reach them. An earlier draft of this
    /// header said "every query", which is the same over-claim class the wave was closing — stated
    /// here so the next reader does not re-derive the wrong universal from a confident sentence.</para>
    /// </summary>
    public readonly struct ConnectionRetargetGrant
    {
        /// <summary>What the holder may do. <c>Refused</c> for a default-constructed grant.</summary>
        public ConnectionRetargetKind Kind { get; }

        /// <summary>
        /// The permission that was consulted, for the refusal message. Null for
        /// <see cref="ConnectionRetargetKind.EstablishOnly"/> and for a default grant, because no
        /// permission was asked in either case.
        /// </summary>
        public string? Permission { get; }

        private ConnectionRetargetGrant(ConnectionRetargetKind kind, string? permission)
        {
            Kind = kind;
            Permission = permission;
        }

        /// <summary>
        /// Asks this caller's own authorization state. A null state is a refusal: a call site with
        /// no user to ask has not established that anyone authorised it.
        /// </summary>
        /// <param name="user">The circuit's <see cref="AppUserState"/>.</param>
        /// <param name="permission">
        /// The permission this surface is registered under. Two are in use on this edge and that is
        /// deliberate, not drift: the dashboard's own retargets sit on <c>run_scripts</c> (round 3's
        /// rule — acting on a connected server) and the two top-bar pickers sit on
        /// <c>execute_checks</c> (RULED 2026-08-02 — whoever may run a check may choose its target).
        /// Each call site names the permission its register entry declares.
        /// </param>
        public static ConnectionRetargetGrant ForCaller(AppUserState? user, string permission) =>
            new(user is not null && user.IsAuthorized(permission)
                    ? ConnectionRetargetKind.Authorised
                    : ConnectionRetargetKind.Refused,
                permission);

        /// <summary>
        /// A bootstrap: bring the process from "no server selected" to a server. The manager
        /// verifies the "no server selected" half itself, so this is a request rather than an
        /// assertion — which is the difference between this and the review note it replaces.
        /// </summary>
        public static ConnectionRetargetGrant Establish =>
            new(ConnectionRetargetKind.EstablishOnly, null);
    }

    /// <summary>
    /// What actually happened to the process-wide current server. Returned rather than thrown so a
    /// caller can render an honest control and an honest sentence: a refusal that leaves a dropdown
    /// showing the rejected choice states the falsehood in the control instead of in the text.
    /// </summary>
    /// <param name="Applied">True when the manager took the write.</param>
    /// <param name="PreviousServerId">The connection id in force before the call.</param>
    /// <param name="CurrentServerId">The connection id in force after it.</param>
    /// <param name="RefusedBecause">Populated when and only when <paramref name="Applied"/> is false.</param>
    public sealed record ConnectionRetargetOutcome(
        bool Applied,
        string? PreviousServerId,
        string? CurrentServerId,
        string? RefusedBecause)
    {
        /// <summary>
        /// True when the write landed AND the target is not the one it was. The distinction is
        /// load-bearing: "the connection was not changed" is a claim about movement, and a write
        /// that set the same id back is not a movement.
        /// </summary>
        public bool Moved =>
            Applied && !string.Equals(PreviousServerId, CurrentServerId, StringComparison.Ordinal);

        internal static ConnectionRetargetOutcome Took(string? previous, string? current) =>
            new(true, previous, current, null);

        internal static ConnectionRetargetOutcome Refuse(string? current, string because) =>
            new(false, current, current, because);
    }
}
