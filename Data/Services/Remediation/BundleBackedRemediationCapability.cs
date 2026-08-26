/* In the name of God, the Merciful, the Compassionate */
/*
 * BundleBackedRemediationCapability — gate 2. The real capability gate that replaces the
 * hard-deny stub. Reads the bundle LIVE on each check (so it tracks bundle activation and
 * deactivation), and fails CLOSED.
 *
 * 2026-08-05 (Adrian's ruling: the Server Configuration feature set is licence-bound): the
 * decision moved into ServerConfigSuiteGate, which adds the Full-TIER binding on top of the
 * remediation claim this class already read. That is the chokepoint for all four features —
 * Remediation, Server Config & Hardening, AG Job Guard and AG Job Sync all reach a server
 * through RemediationRunner, which asks this. A page-level or nav-level gate alone would have
 * left the write path open to direct navigation.
 *
 * DevBridge (--devbridge, dev machine only) unlocks it so the production surface is testable
 * on a dev build — exactly as the dev-tools gate does. Community builds compile the four pages
 * out; real distributions carry no --devbridge and honour the licence.
 */

using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services.Remediation
{
    public sealed class BundleBackedRemediationCapability : IRemediationCapability
    {
        private readonly IBundleAccessor _bundle;

        public BundleBackedRemediationCapability(IBundleAccessor bundle) => _bundle = bundle;

        public bool IsGranted =>
            SQLTriage.Data.BuildMode.DevBridgeActive || ServerConfigSuiteGate.IsAvailable(_bundle);
    }
}
