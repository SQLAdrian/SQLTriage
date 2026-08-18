/* In the name of God, the Merciful, the Compassionate */
/*
 * BundleBackedRemediationCapability — gate 2. The real capability gate that replaces the
 * hard-deny stub. Reads the bundle's remediation claim LIVE on each check (so it tracks
 * bundle activation/deactivation), and fails CLOSED: no explicit claim, no remediation.
 *
 * DevBridge (--devbridge, dev machine only) unlocks it so the production surface is testable
 * on a dev build — exactly as the dev-tools gate does. Community builds compile the surface
 * out; real distributions carry no --devbridge and honour the bundle's remediation claim.
 */

using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services.Remediation
{
    public sealed class BundleBackedRemediationCapability : IRemediationCapability
    {
        private readonly IBundleAccessor _bundle;

        public BundleBackedRemediationCapability(IBundleAccessor bundle) => _bundle = bundle;

        public bool IsGranted =>
            SQLTriage.Data.BuildMode.DevBridgeActive || _bundle.Features.Remediation;
    }
}
