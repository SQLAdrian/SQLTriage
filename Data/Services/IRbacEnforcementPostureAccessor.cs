/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// A NARROW, read-only view of RBAC enforcement posture — the only thing the always-rendered
    /// shell (<c>Components/Layout/MainLayout.razor</c>) needs to raise the global admission banner
    /// (security-8, 2026-08-31). It exposes exactly one member and nothing else: no authorization
    /// decision, no user administration, no config write, no <c>ResolvePrincipal</c>.
    ///
    /// <para><b>Why the shell injects THIS and not <see cref="RbacService"/>.</b> Holding the full
    /// service is what makes a receiver-renamed fail-open gate call spellable from a page —
    /// <c>Pages/ServerDocs.razor</c> carried a dangling <c>@inject RbacService Rbac</c> for a
    /// fortnight and a planted gate through it passed the whole census. The guardrail
    /// <c>RbacServerModeLockoutTests.TheOnlyShippedUiFilesHoldingAnRbacServiceInstanceAreThePinnedThree</c>
    /// pins the set of files that may hold an <see cref="RbacService"/> at exactly three
    /// (Login / Onboarding / Settings — the RBAC administration surfaces). The banner is not an
    /// administration surface: it only READS the posture and links to Settings. Injecting this
    /// interface keeps that pinned set exact, and a page holding only this handle cannot reach
    /// <c>IsAuthorized</c> / <c>HasPermission</c> / <c>AddUser</c> at all — they are not on the
    /// interface, so such a call is a compile error rather than a review finding.</para>
    ///
    /// <para>Backed by the <see cref="RbacService"/> singleton in DI (a forwarding registration),
    /// so the posture the banner reads is byte-identical to the one the startup log and the Settings
    /// banner read from the same object.</para>
    /// </summary>
    public interface IRbacEnforcementPostureAccessor
    {
        /// <summary>
        /// The current enforcement posture. See <see cref="RbacService.DescribeEnforcementPosture"/>.
        /// </summary>
        RbacService.EnforcementPosture DescribeEnforcementPosture();
    }
}
