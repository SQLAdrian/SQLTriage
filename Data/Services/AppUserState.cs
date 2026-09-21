/* In the name of God, the Merciful, the Compassionate */

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:AppUserState.Class — holds the current user's identity for a Blazor circuit and answers authorization
    /// <summary>
    /// Scoped service holding the current user's identity and role for the life of a Blazor
    /// circuit, and the single authorization entry point for the UI.
    ///
    /// <para><b>What is HELD here versus what is ASKED.</b> A circuit lives for hours, so anything
    /// cached in it is a claim about the past. Identity and the socket's loopback fact are held —
    /// they are fixed for the circuit. The role is re-read from the user store on a TTL, because
    /// removing a user has to take effect. Bootstrap eligibility is not held at all: it is asked of
    /// <see cref="RbacService"/> on every read.</para>
    ///
    /// <para><b>Role resolution.</b> This used to build a fresh server-side <c>HttpClient</c> and
    /// GET <c>http://localhost:{port}/auth/me</c>. That request carried none of the browser's
    /// cookies, so <c>/auth/me</c> saw an unauthenticated caller and answered
    /// <c>authenticated=false</c> — every circuit pinned to <b>viewer</b> even with OAuth fully
    /// configured and the user signed in. It now reads the circuit's own principal from
    /// <see cref="AuthenticationStateProvider"/>, which Blazor seeds from the HTTP request that
    /// establishes the circuit. Same principal the auth middleware built; no second request.</para>
    ///
    /// <para><b>Bootstrap eligibility.</b> An unconfigured install behaves as full admin so the
    /// first admin can be configured at all. That hatch is scoped to callers who are already on
    /// the box — see <see cref="IsBootstrapEligible"/>.</para>
    ///
    /// Usage: <c>@inject AppUserState UserState</c> then <c>UserState.IsAuthorized("manage_servers")</c>.
    /// </summary>
    public class AppUserState
    {
        private readonly HostEnvironmentInfo _host;
        private readonly RbacService _rbac;
        private readonly IServiceProvider _services;
        private readonly ILogger<AppUserState> _logger;

        private bool _resolved;
        private string? _cachedRole;
        private string? _identityKey;
        private string? _displayName;
        private string? _provider;
        private string? _sid;
        private RbacService.PrincipalStatus _accountStatus = RbacService.PrincipalStatus.Unknown;
        private bool _storeBacked;
        private DateTime _resolvedAt;

        public AppUserState(
            HostEnvironmentInfo host,
            RbacService rbac,
            IServiceProvider services,
            ILogger<AppUserState> logger)
        {
            _host = host;
            _rbac = rbac;
            _services = services;
            _logger = logger;
        }

        /// <summary>
        /// The current user's role. Defaults to <c>viewer</c> until <see cref="InitAsync"/>
        /// completes — the most restrictive default, so a render that races resolution cannot
        /// escalate. On the WPF desktop <see cref="InitAsync"/> sets admin immediately, so the
        /// effective role there is unchanged.
        /// </summary>
        public string Role
        {
            get
            {
                RefreshFromStoreIfStale();
                return _cachedRole ?? AppRoles.Viewer;
            }
        }

        public bool IsAdmin => Role == AppRoles.Admin;
        public bool IsOperator => Role is AppRoles.Admin or AppRoles.Operator;
        public bool IsViewer => true; // all roles can view

        /// <summary>The signed-in principal's identity key, or empty when unauthenticated.</summary>
        public string IdentityKey => _identityKey ?? string.Empty;

        /// <summary>Display name for the session badge, falling back to the identity key.</summary>
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? IdentityKey : _displayName!;

        /// <summary>True once <see cref="InitAsync"/> has run for this circuit.</summary>
        public bool IsResolved => _resolved;

        /// <summary>True when there is an authenticated principal behind this circuit.</summary>
        public bool IsAuthenticated => !string.IsNullOrEmpty(_identityKey);

        /// <summary>The provider that authenticated this circuit, or empty.</summary>
        public string Provider => _provider ?? string.Empty;

        /// <summary>
        /// What the USER STORE says about the authenticated principal behind this circuit.
        ///
        /// <para>The session cookie is a claim about the past: it carries the role stamped at
        /// sign-in, lives 8 hours and renews on every request while the user keeps browsing. A
        /// user REMOVED or DISABLED in Settings kept full admin from the LAN under enforced RBAC,
        /// across service restarts, for as long as they kept the tab open — remove, disable and
        /// demote were advisory. Measured 2026-08-01. Every circuit now resolves its principal
        /// against the store on <see cref="InitAsync"/> — so a new request or reload sees the
        /// revocation immediately — and re-reads it every <see cref="StoreRoleTtl"/> thereafter,
        /// which is what bounds a circuit that is already open when the account is removed.</para>
        /// </summary>
        public RbacService.PrincipalStatus AccountStatus => _accountStatus;

        /// <summary>
        /// True when this circuit presents an authenticated cookie that the user store no longer
        /// backs — the account was removed or disabled while the session was live. Such a circuit
        /// resolves as <c>viewer</c> regardless of what the cookie claims.
        /// </summary>
        public bool IsPrincipalRevoked
        {
            get
            {
                RefreshFromStoreIfStale();
                return IsAuthenticated && _accountStatus != RbacService.PrincipalStatus.Active;
            }
        }

        /// <summary>
        /// Whether this circuit may use the unconfigured-install bootstrap hatch: the WPF desktop,
        /// or a LOOPBACK connection. False for a remote client.
        ///
        /// <para>The line is loopback because a person on loopback already has the box: they can
        /// stop the service, edit Config\rbac-users.json and restart. The hatch grants them no
        /// authority they lack — it removes an hour of downtime from the recovery path. A remote
        /// unauthenticated client has none of that, and the same hatch would hand them /query and
        /// /server-configuration on a host holding connections to client production servers.</para>
        ///
        /// <para><b>ASKED EVERY TIME, never cached.</b> This used to be a bool field frozen by
        /// <see cref="InitAsync"/> for the life of the circuit, and a circuit is HOURS: one open
        /// tab is one circuit. Measured on 2026-08-03 — an anonymous circuit opened while the
        /// hatch was open, the operator then completed bootstrap, <c>RbacService</c> correctly
        /// answered ineligible, and that circuit went on reporting <c>bootstrapEligible=True</c>
        /// with <see cref="IsAuthorized"/> true for settings, manage_users, run_scripts and
        /// manage_servers — full admin on the term that SHORT-CIRCUITS every permission, behind
        /// every ShellGate, RbacGuard and toolbar control. The class had already solved exactly
        /// this for the role (<see cref="RefreshFromStoreIfStale"/>, <see cref="StoreRoleTtl"/>)
        /// because a circuit outlives a request; the same argument was simply never applied to the
        /// stronger term. Not even a TTL: this is one comparison behind one lock, so there is
        /// nothing to amortise and no window to be stale in.</para>
        /// </summary>
        public bool IsBootstrapEligible =>
            // The WPF desktop has no listener and no auth stack; the process identity is the user.
            !_host.IsBrowserHosted || _rbac.IsBootstrapEligible(IsLoopback);

        /// <summary>
        /// The token that says "the caller behind this call is entitled to the unconfigured-install
        /// bootstrap hatch", and the only thing
        /// <see cref="RbacService.IsAuthorized(string, string, BootstrapEligibilityProof)"/> will
        /// accept in place of the old caller-supplied <c>bool</c>.
        ///
        /// <para><b>Why a type and not a bool (2026-08-17, the capability-token lane).</b> The
        /// chokepoint lane made the raw matrix private and deleted the fail-open two-argument
        /// overload, and the gate that survived still took <c>bootstrapEligible</c> as an ordinary
        /// <c>bool</c>. A page holding an <c>RbacService</c> could therefore write
        /// <c>IsAuthorized(UserState.Role, "settings", true)</c> and reproduce the deleted overload's
        /// semantics exactly — MEASURED at 8959044 by planting that call in
        /// <c>Pages/Settings.razor</c>: it built with 0 errors. A <c>bool</c> is spellable by anyone;
        /// an instance of this type is not, because the only constructor is <c>private</c> to this
        /// nested type and the only code that calls it is this type's own static constructor.</para>
        ///
        /// <para><b>Why the static-constructor handshake, which looks like a puzzle and is not.</b>
        /// The obvious spelling — <see cref="AppUserState"/> holding
        /// <c>= new BootstrapEligibilityProof()</c> — does NOT compile, and that fact is worth
        /// recording because it is the opposite of what the pattern is usually assumed to do: a
        /// nested type may reach its enclosing type's private members, and the enclosing type may NOT
        /// reach the nested type's. MEASURED 2026-08-17: that initialiser on this very field gave
        /// <c>CS0122</c> ("'AppUserState.BootstrapEligibilityProof.BootstrapEligibilityProof()' is
        /// inaccessible due to its protection level"). So the mint runs where the constructor IS
        /// reachable — inside the type — and the instance is handed OUT to a private field of the
        /// enclosing class, in the one direction the language allows. No code in the assembly can
        /// NAME that constructor, this class included (<c>CS0122</c>, measured); code that declines to
        /// name it can still reach it, which is the paragraph on <c>[UnsafeAccessor]</c> below.</para>
        ///
        /// <para><b>Why a class and not a struct.</b> <c>default(T)</c> on a struct is a legal,
        /// constructor-free instance, so a struct token would hand the hatch straight back:
        /// <c>IsAuthorized(role, perm, default)</c> would be a minted proof. A reference type's
        /// <c>default</c> is <c>null</c>, and null is the DENY case — MEASURED: that exact line
        /// planted in <c>Pages/Settings.razor</c> builds with 0 errors and denies at runtime.
        /// <c>sealed</c> for the matching reason: a derived type would carry its own constructor, so
        /// sealing removes the question (a subclass plant is <c>CS0509</c>, measured).</para>
        ///
        /// <para><b>What this token proves and what it does NOT.</b> It proves WHO minted it —
        /// this type, once, at type initialisation. It carries no state, so it says nothing about
        /// WHICH circuit asked or WHEN. The freshness lives at the single hand-over site below, which
        /// reads <see cref="IsBootstrapEligible"/> on every call, exactly as the plain bool did. The
        /// token is a capability, not a certificate.</para>
        ///
        /// <para><b>Accessibility alone was NOT enough, and the correction is recorded rather than
        /// quietly applied (2026-08-17, the fix round of this same lane).</b> The first draft of this
        /// type rested entirely on <c>private</c>, and the lane's adversarial verifier broke it in
        /// source that compiles. <c>[UnsafeAccessor(UnsafeAccessorKind.Constructor)]</c> is a
        /// declaration, not a reflection API, and it ignores accessibility by design: a private
        /// <c>extern</c> declaration anywhere in the assembly mints this type, with no
        /// <c>System.Reflection</c>, no <c>Activator</c> and no <c>typeof</c>. MEASURED on this tree —
        /// a helper in <c>Data/Services</c> carrying that attribute plus one call from
        /// <c>Pages/Settings.razor</c> BUILT with 0 errors and, before the identity check below,
        /// GRANTED a viewer the <c>settings</c> permission on a dormant install where the same call
        /// with <c>null</c> denied. No C# accessibility modifier stops that shape, so no
        /// constructor-based design can close it; the close is at RUNTIME, in
        /// <see cref="IsTheMintedProof"/>, which asks whether the token IS the one instance rather
        /// than whether one exists.</para>
        ///
        /// <para><b>Not closed by this either, stated so nobody reads it as more:</b> THEFT of the
        /// minted instance. Anything in the process that can read a private static field —
        /// reflection, or <c>[UnsafeAccessor(UnsafeAccessorKind.StaticField)]</c> — can take the real
        /// token and pass it, and the identity check will accept it, because it IS the token. That is
        /// not a hole a type system can close: it is the ordinary fact that in-process code with
        /// arbitrary reflection is inside the trust boundary. It is held by a LINT, not by the
        /// language: <c>RbacChokepointTests.NothingInTheAssemblyReachesTheBootstrapProof</c> censuses
        /// the whole shipped assembly (Pages and Components compile into it) for any member typed on
        /// this proof at any accessibility, and
        /// <c>RbacChokepointTests.NothingInTheAssemblyDeclaresAnUnsafeAccessor</c> bans the
        /// accessibility-bypassing declaration outright. Both plant shapes were MEASURED red against
        /// those two. The test suite steals the token deliberately, in one named helper, because it is
        /// the only way another assembly can exercise the eligible branch.</para>
        /// </summary>
        public sealed class BootstrapEligibilityProof
        {
            /// <summary>
            /// Private, which closes every spelling that NAMES it. A page naming it gets
            /// <c>CS0122</c> ("inaccessible due to its protection level") — measured 2026-08-17 in
            /// <c>Pages/Settings.razor</c>, and measured again from <see cref="AppUserState"/> itself,
            /// which is why the mint sits in the static constructor below.
            ///
            /// <para>It is NOT the whole mechanism, and the earlier draft of this comment said it
            /// was. <c>[UnsafeAccessor(UnsafeAccessorKind.Constructor)]</c> reaches a private
            /// constructor without naming it and compiles clean (measured). What makes a second
            /// instance worthless is <see cref="IsTheMintedProof"/>, not this modifier.</para>
            /// </summary>
            private BootstrapEligibilityProof() { }

            /// <summary>
            /// The ONLY call to that constructor in the assembly. It runs once, at type
            /// initialisation, and hands the single instance to the enclosing class's private field —
            /// a nested type may reach its enclosing type's private members, which is the direction
            /// the language does allow.
            /// </summary>
            static BootstrapEligibilityProof()
            {
                _mintedProof = new BootstrapEligibilityProof();
            }
        }

        /// <summary>
        /// The one minted token, held once because it carries no state. Written exactly once, by
        /// <see cref="BootstrapEligibilityProof"/>'s static constructor, and <c>private</c> here so
        /// no page can reach the instance either — closing the type's constructor and then exposing
        /// an instance of it would be the same hatch with an extra step. Handed to the gate only on
        /// the branch where <see cref="IsBootstrapEligible"/> is true; every other call passes
        /// <c>null</c>, which denies.
        /// </summary>
        private static BootstrapEligibilityProof? _mintedProof;

        /// <summary>
        /// <b>THE runtime chokepoint on the hatch term.</b> True only for the ONE instance this class
        /// minted. <see cref="RbacService.IsAuthorized(string, string, BootstrapEligibilityProof)"/>
        /// asks this instead of testing the argument for null, so a token that merely EXISTS is not a
        /// claim — being the token is.
        ///
        /// <para><b>Why identity and not presence (2026-08-17, the fix round).</b> Presence was the
        /// original design and it was broken by measurement, not by argument:
        /// <c>[UnsafeAccessor(UnsafeAccessorKind.Constructor)]</c> mints this type from anywhere in
        /// the assembly, in source that compiles with 0 errors and uses no reflection API, so
        /// "a token exists" was a claim any file could manufacture. A forged instance is not
        /// reference-equal to <see cref="_mintedProof"/>, so it now denies —
        /// <c>RbacChokepointTests.AForgedProofIsDeniedWhereTheMintedProofGrants</c> pins exactly that,
        /// with the same service, role and permission on both sides so only identity differs.</para>
        ///
        /// <para><b>What it does not do.</b> It does not stop code that STEALS the minted instance out
        /// of the private field above (reflection, or <c>UnsafeAccessorKind.StaticField</c>): that
        /// token is the real one and is accepted. Nothing in-process closes that, and the census in
        /// <c>RbacChokepointTests</c> is what stands against it.</para>
        ///
        /// <para>Returning a <c>bool</c> is deliberate: this method hands out a verdict about a token
        /// the caller already holds, never a token, so exposing it to the assembly gives a page
        /// nothing it did not have. It is <c>internal</c> because <c>RbacService</c> is a different
        /// file in the same assembly and <c>private</c> could not serve it.</para>
        /// </summary>
        internal static bool IsTheMintedProof(BootstrapEligibilityProof? proof) =>
            proof is not null && ReferenceEquals(proof, _mintedProof);

        /// <summary>
        /// Forces the nested type's initialiser, because nothing else in the process touches that
        /// type: <see cref="_mintedProof"/> is a field of THIS class, so reading it does not trigger
        /// the nested static constructor that fills it.
        ///
        /// <para>Deterministic, not hopeful: <c>RunClassConstructor</c> blocks until the type
        /// initialiser has completed, and a type initialiser runs at most once. If this call were
        /// ever removed the field would stay null, every circuit would be treated as
        /// bootstrap-INELIGIBLE, and an unconfigured install would lock its own operator out —
        /// a denial, never an escalation. That direction is deliberate, and it is not a guess:
        /// removing this line was MEASURED on 2026-08-17 to take
        /// <c>RbacBootstrapScopeTests</c> from green to 7 failed / 27 passed.</para>
        ///
        /// <para><b>A liveness hazard the handshake carries, stated because it is real and has not
        /// been exercised.</b> The two type initialisers reference each other: this one forces the
        /// token type, and the token type writes a static field of THIS type. A thread entering from
        /// each end at the same moment can hold one type-init lock and wait on the other. Read from
        /// the source, not observed — several thousand test cases and every live run so far have
        /// completed, and no race harness has been written. In practice the app touches
        /// <see cref="AppUserState"/> first from a single startup path; the test suite is the only
        /// caller that reaches the token type directly. If a hang is ever seen at first
        /// authorization, this is the first place to look.</para>
        /// </summary>
        static AppUserState()
        {
            RuntimeHelpers.RunClassConstructor(typeof(BootstrapEligibilityProof).TypeHandle);
        }

        /// <summary>
        /// The authorization gate for every UI surface. Combines the role with this circuit's
        /// bootstrap eligibility, which is the part
        /// <see cref="RbacService.IsAuthorized(string, string, BootstrapEligibilityProof)"/> cannot
        /// know on its own — this method is where that term is supplied, and since 2026-08-17 it is
        /// the only way a page can reach the decision at all (the raw matrix is private, the
        /// fail-open two-argument overload is deleted, and the surviving gate's third argument is
        /// <see cref="BootstrapEligibilityProof"/>, which only this class holds).
        ///
        /// <para><b>Second layer since round 8 (2026-08-03), and it matters which layer this is.</b>
        /// A circuit only exists here at all because <see cref="InteractiveAppAdmission"/> already
        /// admitted the request: the caller is on loopback, or they hold a session cookie. So this
        /// method is no longer deciding anything for an ANONYMOUS stranger on the network — that
        /// caller never reaches a circuit. It decides what a named, authenticated principal may do,
        /// and what the console user may do on an unconfigured install. A gap here is a privilege
        /// escalation between roles; it is no longer an anonymous compromise. Both facts are worth
        /// holding: the second is why the lane could stop enumerating controls, and the first is
        /// why this method still has to be right.</para>
        /// </summary>
        public bool IsAuthorized(string permission)
        {
            // The WPF desktop is a single-user surface with no listener and no auth.
            if (!_host.IsBrowserHosted) return true;

            RefreshFromStoreIfStale();

            // The single hand-over site: the token is minted once at type init, and THIS is the
            // only place it leaves the class. IsBootstrapEligible is read here, on every call, and
            // the token goes across only when it answers true — the same term the plain bool
            // carried, now unspellable anywhere else.
            return _rbac.IsAuthorized(Role, permission, IsBootstrapEligible ? _mintedProof : null);
        }

        /// <summary>
        /// How long a store-resolved role is trusted before it is re-read.
        ///
        /// <para>A Blazor circuit outlives a request: one open tab is one circuit for hours, and
        /// <see cref="InitAsync"/> is idempotent, so resolving only at init would leave a revoked
        /// admin in place until the user reloaded the page. Blazor renders call
        /// <see cref="IsAuthorized"/> and <see cref="Role"/> constantly, so this is a TTL rather
        /// than a per-call lookup: bounded staleness, no lock storm. The store is a small in-memory
        /// list behind a lock, so the re-read is a linear scan of tens of records.</para>
        /// </summary>
        internal static readonly TimeSpan StoreRoleTtl = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Re-reads the role from the user store when the cached answer is older than
        /// <see cref="StoreRoleTtl"/>. No-op for the WPF desktop, for an unauthenticated circuit,
        /// and for a role planted by <see cref="SetRole"/> (tests, WPF) — nothing there was
        /// store-backed to begin with.
        /// </summary>
        private void RefreshFromStoreIfStale()
        {
            if (!_storeBacked) return;
            if (DateTime.UtcNow - _resolvedAt < StoreRoleTtl) return;

            var resolved = _rbac.ResolvePrincipal(_provider, _identityKey, _sid);
            _resolvedAt = DateTime.UtcNow;

            if (resolved.Status == _accountStatus && resolved.Role == _cachedRole) return;

            _logger.LogInformation(
                "AppUserState — store re-check for {Principal}: {OldStatus}/{OldRole} → {NewStatus}/{NewRole}",
                _identityKey, _accountStatus, _cachedRole, resolved.Status, resolved.Role);

            _accountStatus = resolved.Status;
            _cachedRole = resolved.Role;
        }

        /// <summary>
        /// L3 of the lockout guard — break-glass, for the two surfaces that can undo a bad RBAC
        /// configuration and nothing else.
        ///
        /// <para>L1 and L2 make it hard to enforce RBAC into an unusable state, but a config can
        /// still go bad out from under the app: an OAuth secret rotates, a domain becomes
        /// unreachable, rbac-config.json is hand-edited. A request arriving on LOOPBACK keeps
        /// access to Settings and Onboarding regardless, so the documented recovery stops being
        /// "delete Config\rbac-users.json on disk" and becomes "RDP to the box and browse to
        /// http://localhost:&lt;port&gt;/settings".</para>
        ///
        /// <para>Deliberately a separate method rather than a rule inside <see cref="IsAuthorized"/>:
        /// the <c>settings</c> permission also gates /service-management (installs and stops the
        /// Windows service), /audit-log and the report-bundle owner editor. Break-glass must not
        /// leak to those, and a distinct, greppable call site is the only way to be sure it
        /// cannot.</para>
        ///
        /// <para><b>The recovery-password gate on the break-glass term (adminguard-breakglass,
        /// 2026-09-01).</b> The break-glass grant on these two surfaces can now be bound behind the
        /// store-independent AdminGuard recovery password (<see cref="AdminAuthService"/>, secret in
        /// appsettings, not the RBAC store). The first branch below is deliberately
        /// <see cref="IsAuthorizedByRole"/> — role-only, with a <c>null</c> bootstrap proof — NOT
        /// <see cref="IsAuthorized"/>: a genuinely role-authorised admin (or the WPF desktop) is
        /// unchanged and never sees a second password, but the loopback BOOTSTRAP hatch is itself a
        /// break-glass grant and is gated here rather than admitted by the first branch. Everything
        /// that reaches the loopback term is therefore a break-glass grant — the never-configured
        /// bootstrap hatch OR the lapsed-store recovery term — and both are subject to
        /// <see cref="BreakGlassCredentialSatisfied"/>.</para>
        ///
        /// <para><b>⚠ DELIBERATE B1 (SEC-3 break-glass) AMENDMENT — Adrian's explicit ruling of
        /// 2026-09-01 (Interpretation 2).</b> A password-SET install no longer break-glasses
        /// unconditionally on loopback even when RBAC is NEVER configured (posture Off): the local
        /// caller must unlock with the recovery password first. This intentionally departs from the
        /// 2026-08-26 B1/SEC-3 ruling that the never-configured loopback surface opens
        /// unconditionally. The NO-password never-configured case stays byte-identical B1 — no
        /// password means <see cref="BreakGlassCredentialSatisfied"/> is true (the cold-start
        /// carve-out), so a fresh install still onboards and mints the first admin. Recorded here
        /// per the reporting rule; the DECISIONS entry in the private meta repo is written at lane close.</para>
        /// </summary>
        public bool IsAuthorizedWithBreakGlass(string permission)
        {
            // A principal authorised by ROLE — a real signed-in admin, or the WPF desktop — is
            // unchanged: no second password, ever. Deliberately role-only (null bootstrap proof),
            // because the loopback bootstrap hatch IS a break-glass grant and is gated below.
            if (IsAuthorizedByRole(permission)) return true;

            // Every grant that reaches here is a LOOPBACK BREAK-GLASS: the never-configured
            // bootstrap hatch or the lapsed-store recovery term. Both bind to the recovery password
            // WHENEVER one is set (Interpretation 2). No password → open (cold-start carve-out).
            return _host.IsBrowserHosted && IsLoopback && BreakGlassCredentialSatisfied();
        }

        /// <summary>
        /// Authorization by ROLE alone — the same decision as <see cref="IsAuthorized"/> but with a
        /// <c>null</c> bootstrap proof, so the unconfigured-install loopback hatch never satisfies
        /// it. A real admin (or the WPF desktop) passes; an anonymous loopback viewer on a
        /// never-configured install does NOT (RbacService returns
        /// <c>HasPermission(viewer, permission)</c> = false in posture Off without the proof). This
        /// is what lets <see cref="IsAuthorizedWithBreakGlass"/> gate the bootstrap-hatch break-glass
        /// behind the recovery password while leaving an authenticated admin untouched.
        /// </summary>
        private bool IsAuthorizedByRole(string permission)
        {
            if (!_host.IsBrowserHosted) return true;
            RefreshFromStoreIfStale();
            return _rbac.IsAuthorized(Role, permission, null);
        }

        /// <summary>
        /// Whether the break-glass recovery credential is satisfied for the loopback term of
        /// <see cref="IsAuthorizedWithBreakGlass"/>.
        ///
        /// <para><b>Cold-start carve-out (unbrickable by construction).</b> No AdminGuard password —
        /// or an AdminAuthService that cannot be resolved from this circuit's provider — leaves the
        /// break-glass OPEN. A fresh/never-configured install has no password, so a local operator
        /// still onboards and mints the first admin; a later store-damage on such an install is
        /// still recoverable from the box. This is the byte-identical B1 case.</para>
        ///
        /// <para><b>Interpretation 2.</b> Once a recovery password IS set, in ANY posture, the local
        /// caller must unlock (<see cref="AdminAuthService.IsUnlocked"/>) before the break-glass
        /// grants. Unlocking opens the recovery FORM; it does not sign the caller in — see the
        /// no-self-elevation note on the loopback-admission lane.</para>
        /// </summary>
        private bool BreakGlassCredentialSatisfied()
        {
            var adminAuth = ResolveAdminAuth();
            if (adminAuth is null || !adminAuth.HasPassword) return true;
            return adminAuth.IsUnlocked;
        }

        /// <summary>
        /// The break-glass posture of THIS circuit, for a recovery surface to render the right thing
        /// when <see cref="IsAuthorizedWithBreakGlass"/> withholds the page. See
        /// <see cref="BreakGlassPosture"/>.
        /// </summary>
        public BreakGlassPosture BreakGlassState
        {
            get
            {
                if (!(_host.IsBrowserHosted && IsLoopback)) return BreakGlassPosture.NotEligible;
                var adminAuth = ResolveAdminAuth();
                if (adminAuth is null || !adminAuth.HasPassword) return BreakGlassPosture.OpenNoPassword;
                return adminAuth.IsUnlocked ? BreakGlassPosture.Unlocked : BreakGlassPosture.LockedNeedsPassword;
            }
        }

        /// <summary>
        /// Resolves the store-independent break-glass gate for this circuit, or null when it is not
        /// registered in this circuit's provider (unit tests with an empty provider — treated as the
        /// cold-start carve-out). Every shipped host registers it, so null is a test-only state.
        /// </summary>
        private AdminAuthService? ResolveAdminAuth()
        {
            try { return _services.GetService<AdminAuthService>(); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AppUserState: AdminAuthService unavailable; break-glass treated as open (cold-start)");
                return null;
            }
        }

        /// <summary>
        /// What a loopback recovery surface should show when
        /// <see cref="IsAuthorizedWithBreakGlass"/> has withheld it.
        /// </summary>
        public enum BreakGlassPosture
        {
            /// <summary>Not a loopback browser circuit — the caller has no break-glass claim at all
            /// (a remote client, or the WPF desktop, which is authorised by role instead).</summary>
            NotEligible,

            /// <summary>Loopback, and no recovery password is set — break-glass is OPEN (the
            /// cold-start carve-out). The surface renders normally.</summary>
            OpenNoPassword,

            /// <summary>Loopback, a recovery password IS set, and this session has not unlocked it —
            /// the surface renders the unlock overlay instead of AccessDenied.</summary>
            LockedNeedsPassword,

            /// <summary>Loopback, a recovery password is set, and this session has unlocked it — the
            /// recovery form is reachable (unlocking does NOT grant an admin session).</summary>
            Unlocked,
        }

        /// <summary>True when the connection behind this circuit arrived over loopback.</summary>
        public bool IsLoopback { get; private set; }

        /// <summary>
        /// The principal to stamp on a change-control record as the human who approved a change —
        /// or <c>null</c> when no human can be named, in which case the caller MUST NOT claim
        /// approval.
        ///
        /// <para><b>Why this exists.</b> Every remediation apply site passed
        /// <c>approvedBy: Environment.UserName</c>, which is the PROCESS identity. On the WPF
        /// desktop that is right — the person at the keyboard is the process owner. Everywhere
        /// else it is wrong, and on the installed service it is wrong in the worst way: the
        /// service runs as the virtual account <c>NT SERVICE\SQLTriage</c>, so a change pushed to
        /// a client's production server was recorded in the audit ledger as "approved by
        /// SQLTriage" no matter who clicked it. For a product whose deliverable IS the audit
        /// trail, an approval record naming the wrong principal is worse than a missing one.</para>
        ///
        /// <para><b>Why null rather than a placeholder.</b> A browser-hosted circuit can reach an
        /// apply button without being authenticated — the loopback bootstrap hatch opens the
        /// gated pages on an unconfigured install. That caller is authorised to act but cannot be
        /// NAMED, and a change-control record that names "localhost" as the approver is a
        /// fabricated attestation. Callers pass <c>approved: ApprovingPrincipal is not null</c>,
        /// so the runner's gate 4 refuses with "Remediation requires explicit human approval"
        /// instead of applying under a fictional approver.</para>
        /// </summary>
        public string? ApprovingPrincipal
        {
            get
            {
                // WPF desktop: single user, no listener, no auth stack. The process identity IS
                // the person who clicked, and it is the only identity that exists.
                if (!_host.IsBrowserHosted) return Environment.UserName;

                if (!IsAuthenticated) return null;

                // A revoked or disabled principal is not an approver either — it resolves to
                // viewer for authorization, so it must not be recorded as having approved.
                if (IsPrincipalRevoked) return null;

                return _identityKey;
            }
        }

        /// <summary>
        /// Resolves the role and bootstrap eligibility for this circuit. Idempotent — safe to
        /// call from every layout and page.
        /// </summary>
        public async Task InitAsync()
        {
            if (_resolved) return;

            // WPF desktop — single user, always admin, no auth stack to consult.
            if (!_host.IsBrowserHosted)
            {
                _cachedRole = AppRoles.Admin;
                IsLoopback = true;
                _resolved = true;
                return;
            }

            ClaimsPrincipal? principal = null;
            try
            {
                // The circuit's own principal. Blazor seeds ServerAuthenticationStateProvider
                // from HttpContext.User of the request that establishes the circuit, so this is
                // the exact principal the auth middleware built — including the loopback claim
                // stamped by SqlTriageAuth.
                var provider = _services.GetService<AuthenticationStateProvider>();
                if (provider != null)
                    principal = (await provider.GetAuthenticationStateAsync()).User;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AppUserState — AuthenticationStateProvider threw; falling back to HttpContext");
            }

            if (principal?.Identity?.IsAuthenticated == true)
            {
                _identityKey = principal.FindFirstValue(SqlTriageAuthClaims.IdentityKey)
                               ?? principal.FindFirstValue(ClaimTypes.Email)
                               ?? principal.Identity.Name;
                _displayName = principal.FindFirstValue(ClaimTypes.Name) ?? _identityKey;
                _provider = principal.FindFirstValue(SqlTriageAuthClaims.Provider)
                            ?? principal.FindFirstValue("provider")
                            ?? string.Empty;

                // The STORE decides the role, not the cookie. See the remarks on
                // AccountStatus: a cookie is a claim about the past, and removing a user has to
                // take effect on their next request, not in eight hours.
                _sid = principal.FindFirstValue(ClaimTypes.PrimarySid);
                var resolved = _rbac.ResolvePrincipal(_provider, _identityKey, _sid);

                _accountStatus = resolved.Status;
                _cachedRole = resolved.Role;
                _storeBacked = true;
                _resolvedAt = DateTime.UtcNow;

                if (resolved.Status != RbacService.PrincipalStatus.Active)
                {
                    var claimedRole = principal.FindFirstValue(ClaimTypes.Role) ?? "(none)";
                    _logger.LogWarning(
                        "AppUserState — session cookie for {Principal} ({Provider}) claims role {ClaimedRole}, but the "
                        + "user store says {Status}. Resolving as {Role}; the cookie is not evidence of a live account.",
                        _identityKey, _provider, claimedRole, resolved.Status, _cachedRole);
                }
            }
            else
            {
                _cachedRole = AppRoles.Viewer;
                _accountStatus = RbacService.PrincipalStatus.Unknown;
            }

            // The loopback FACT is resolved once, because it is a property of this circuit's
            // socket and cannot change while the circuit lives. Eligibility is NOT stored here:
            // IsBootstrapEligible asks RbacService on every read, so this circuit can never go on
            // asserting a hatch the service has stopped granting. See that property.
            IsLoopback = ReadLoopback(principal);
            _resolved = true;

            _logger.LogDebug(
                "AppUserState resolved: role={Role}, authenticated={Auth}, loopback={Loopback}, bootstrapEligible={Bootstrap}",
                _cachedRole, IsAuthenticated, IsLoopback, IsBootstrapEligible);
        }

        /// <summary>
        /// Reads the loopback fact. Prefers the claim stamped by the auth middleware, which is
        /// the only source that survives into an interactive circuit; falls back to
        /// <see cref="IHttpContextAccessor"/>, which is populated during the server-side render
        /// but null once the circuit is live.
        ///
        /// <para>Absent both, the answer is FALSE — the safe direction. A missing signal must not
        /// mint an admin.</para>
        /// </summary>
        private bool ReadLoopback(ClaimsPrincipal? principal)
        {
            var claim = principal?.FindFirstValue(SqlTriageAuthClaims.Loopback);
            if (claim != null)
                return string.Equals(claim, "true", StringComparison.OrdinalIgnoreCase);

            try
            {
                var ctx = _services.GetService<IHttpContextAccessor>()?.HttpContext;
                if (ctx != null)
                    return IsLoopbackAddress(ctx.Connection.RemoteIpAddress);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AppUserState — HttpContext unavailable for the loopback check");
            }

            _logger.LogWarning(
                "AppUserState — no loopback signal on this circuit; treating it as REMOTE. "
                + "The bootstrap hatch stays closed, so an unconfigured install will render as viewer.");
            return false;
        }

        /// <summary>
        /// Loopback test for a connection address.
        ///
        /// <para>The IPv4-mapped unwrap is load-bearing: both hosts bind with
        /// <c>ListenAnyIP</c>, which is dual-stack, so a v4 client arrives as
        /// <c>::ffff:127.0.0.1</c> and a plain <c>IPAddress.IsLoopback</c> says false.</para>
        ///
        /// <para>Deliberately NOT <c>ApiEndpoints.IsLocalOrSameOriginRequest</c>: that reads
        /// <c>Request.Host.Host</c>, an attacker-controlled header —
        /// <c>curl -H "Host: localhost" http://box:5155/…</c> passes it from anywhere on the
        /// network. Fine as the CSRF heuristic it is; unfit as a trust boundary.</para>
        /// </summary>
        public static bool IsLoopbackAddress(IPAddress? address)
        {
            if (address == null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return IPAddress.IsLoopback(address);
        }

        /// <summary>
        /// Sets the role directly — WPF mode and unit tests. Marks the circuit resolved so a
        /// later <see cref="InitAsync"/> cannot overwrite what a test just established.
        /// </summary>
        public void SetRole(string role)
        {
            _cachedRole = role;
            _resolved = true;
        }

        /// <summary>
        /// Forces the next role read to consult the store again. Test seam for the revocation
        /// path — the production trigger is <see cref="StoreRoleTtl"/> elapsing.
        /// </summary>
        internal void ExpireStoreCacheForTests() => _resolvedAt = DateTime.MinValue;

        /// <summary>
        /// Plants the circuit's loopback fact — WPF and unit tests, standing in for the claim the
        /// auth middleware stamps.
        ///
        /// <para>This replaced <c>SetBootstrapEligible(eligible, loopback)</c>, which could plant
        /// the two INDEPENDENTLY. A seam that lets a test assert an eligibility the service would
        /// never have granted is a seam that tests the test; worse, it kept the belief that
        /// eligibility is a value a circuit holds rather than a question it asks. There is one
        /// input now, and <see cref="IsBootstrapEligible"/> derives the rest live.</para>
        /// </summary>
        internal void SetLoopbackForTests(bool loopback)
        {
            IsLoopback = loopback;
            _resolved = true;
        }
    }
}
