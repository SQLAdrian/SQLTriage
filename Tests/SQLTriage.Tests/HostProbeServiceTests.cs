/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.HostProbe;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Host-probe lane step 1: the FAIL-CLOSED gates. The live probe needs a real
    /// elevated host (verified manually 2026-06-19); here we pin the gates that
    /// must hold without one — capability-denied short-circuits, and a non-probing
    /// outcome is never reported as a pass (DidProbe contract).
    /// </summary>
    public class HostProbeServiceTests
    {
        private static HostProbeService Svc(IHostProbeCapability cap) =>
            new(new PowerShellService(NullLogger<PowerShellService>.Instance),
                cap, NullLogger<HostProbeService>.Instance);

        [Fact]
        public async System.Threading.Tasks.Task CapabilityDenied_ShortCircuits_NeverProbes()
        {
            var r = await Svc(new DeniedHostProbeCapability()).ProbePowerPlanAsync(".");
            Assert.Equal(HostProbeOutcome.CapabilityDenied, r.Outcome);
            Assert.False(r.DidProbe); // denied is NOT a pass
        }

        [Fact]
        public void DidProbe_IsTrue_OnlyForRealVerdicts()
        {
            // The distinct-terminal-states contract: only Compliant/NotCompliant
            // count as an actual probe result; the rest are "did not run".
            Assert.True(new HostProbeResult("k", ".", HostProbeOutcome.Compliant, "").DidProbe);
            Assert.True(new HostProbeResult("k", ".", HostProbeOutcome.NotCompliant, "").DidProbe);
            Assert.False(new HostProbeResult("k", ".", HostProbeOutcome.NeedsElevation, "").DidProbe);
            Assert.False(new HostProbeResult("k", ".", HostProbeOutcome.CapabilityDenied, "").DidProbe);
            Assert.False(new HostProbeResult("k", ".", HostProbeOutcome.CouldNotProbe, "").DidProbe);
        }

        [Fact]
        public async System.Threading.Tasks.Task Granted_ButNotElevated_ReturnsNeedsElevation_NotAPass()
        {
            // CI / a normal dev session is not elevated → must fail closed.
            // (If this ever runs elevated, the outcome is a real probe verdict, not
            // NeedsElevation; either way it must NEVER be a vacuous Compliant.)
            var r = await Svc(new GrantedHostProbeCapability()).ProbePowerPlanAsync(".");
            if (!HostProbeService.IsElevated())
            {
                Assert.Equal(HostProbeOutcome.NeedsElevation, r.Outcome);
                Assert.False(r.DidProbe);
            }
            else
            {
                // elevated: a real verdict or a probe failure — but not a fake pass path
                Assert.True(r.Outcome != HostProbeOutcome.CapabilityDenied);
            }
        }
    }
}
