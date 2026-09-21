/* In the name of God, the Merciful, the Compassionate */
/*
 * BundleBackedHostProbeCapability — the real host-probe capability gate, mirroring
 * BundleBackedRemediationCapability. Host probes cross the SQL->OS/AD trust boundary, so
 * (like write-remediation) they fail CLOSED: no explicit bundle grant, no probing. Read LIVE
 * each call so the gate tracks bundle activation/deactivation.
 *
 * DevBridge (--devbridge, dev machine only) unlocks it so the surface is testable on a dev
 * build. Real distributions carry no --devbridge and honour the bundle's HostProbe claim. The
 * elevation gate (HostProbeService.IsElevated) stacks on top as a second independent fail-closed
 * check, so even a granted capability cannot probe without an elevated session.
 */

using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services.HostProbe
{
    public sealed class BundleBackedHostProbeCapability : IHostProbeCapability
    {
        private readonly IBundleAccessor _bundle;

        public BundleBackedHostProbeCapability(IBundleAccessor bundle) => _bundle = bundle;

        public bool IsGranted =>
            SQLTriage.Data.BuildMode.DevBridgeActive || _bundle.Features.HostProbe;
    }
}
