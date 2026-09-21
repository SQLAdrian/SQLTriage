/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using SQLTriage.Data.Services;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The one place in the test suite that mints an
    /// <see cref="AppUserState.BootstrapEligibilityProof"/>, and the reason it needs a comment.
    ///
    /// <para>Since 2026-08-17 the third argument of
    /// <see cref="RbacService.IsAuthorized(string, string, AppUserState.BootstrapEligibilityProof)"/>
    /// is a capability token whose constructor is private to the token type, minted once by that
    /// type's static constructor and handed to a private field of <see cref="AppUserState"/>. No
    /// caller can WRITE one — that is the point, and it is what closes the last shape in which a
    /// shipped page could authorise itself by passing a literal <c>true</c>.</para>
    ///
    /// <para><b>So a test that needs the eligible branch has exactly one route: STEAL the minted
    /// instance.</b> Not mint a second one — since the fix round of 2026-08-17 the gate tests
    /// identity (<c>AppUserState.IsTheMintedProof</c>), so a freshly constructed token DENIES, which
    /// is what <see cref="Forged"/> exists to demonstrate. This helper reads the one instance out of
    /// <c>AppUserState</c>'s private static field by reflection, deliberately, in one named place, so
    /// the theft is greppable rather than scattered.</para>
    ///
    /// <para><b>That theft is also the honest statement of what the token does NOT close.</b> Code
    /// that can read a private static field — reflection here, or
    /// <c>[UnsafeAccessor(UnsafeAccessorKind.StaticField)]</c> in shipped source — holds the real
    /// token and is granted, because it IS the real token. No type system closes that; the census in
    /// <c>RbacChokepointTests</c> is what stands against it, together with
    /// <c>RbacServerModeLockoutTests.NoShippedUiComputesAnAuthorizationDecisionItself</c>, which bans
    /// a page computing the verdict at all regardless of the third argument.</para>
    ///
    /// <para>The alternative — routing these assertions through
    /// <see cref="AppUserState.IsAuthorized(string)"/> — was rejected because the three call sites
    /// that need this are asserting about <see cref="RbacService"/>'s own two-branch behaviour with
    /// the hatch term forced ON, which is precisely the thing a circuit will not let you force.</para>
    /// </summary>
    internal static class BootstrapProofForTests
    {
        /// <summary>
        /// THE minted proof — the very instance <see cref="AppUserState"/> holds, read out of its
        /// private static field. Identity is the whole claim now, so an equivalent instance would not
        /// do: this has to be the same reference the product code hands to the gate.
        /// </summary>
        internal static AppUserState.BootstrapEligibilityProof Minted { get; } = Steal();

        /// <summary>
        /// A second, freshly constructed instance — a FORGERY. Indistinguishable from
        /// <see cref="Minted"/> in type and (absent) state, and denied by the gate, which is the
        /// property <c>RbacChokepointTests.AForgedProofIsDeniedWhereTheMintedProofGrants</c> pins.
        /// This is the test-suite stand-in for the shape that broke the first draft of the lane: an
        /// <c>[UnsafeAccessor(UnsafeAccessorKind.Constructor)]</c> declaration in shipped source,
        /// which produces exactly this — a real token of the right type that we did not mint.
        /// </summary>
        internal static AppUserState.BootstrapEligibilityProof Forged { get; } =
            (AppUserState.BootstrapEligibilityProof)Activator.CreateInstance(
                typeof(AppUserState.BootstrapEligibilityProof), nonPublic: true)!;

        private static AppUserState.BootstrapEligibilityProof Steal()
        {
            // The field is filled by the token type's static constructor, which AppUserState's own
            // static constructor forces; touching AppUserState here is what guarantees both have run.
            RuntimeHelpers.RunClassConstructor(typeof(AppUserState).TypeHandle);

            var field = typeof(AppUserState).GetField(
                "_mintedProof", BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null)
                throw new InvalidOperationException(
                    "AppUserState._mintedProof is gone. The tests read the minted token out of that "
                    + "field because the gate now accepts only that instance; if the field was "
                    + "renamed, rename it here too.");

            return (AppUserState.BootstrapEligibilityProof?)field.GetValue(null)
                ?? throw new InvalidOperationException(
                    "AppUserState._mintedProof is null after forcing the type initialisers. The mint "
                    + "handshake is broken, which would lock an unconfigured install out of its own "
                    + "bootstrap hatch.");
        }
    }
}
