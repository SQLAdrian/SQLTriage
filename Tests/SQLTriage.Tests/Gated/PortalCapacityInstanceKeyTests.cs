/* In the name of God, the Merciful, the Compassionate */

// Extracted from ServerAddressCommaPortTests.cs on 2026-08-04. This is the ONE test in that
// 13-test fixture that binds a community-gated symbol: SQLTriage.Data.Services.Portal.
// CapacityCollector lives under Data\Services\Portal\**, which buildprofile.targets
// Compile-Removes from the community build (SQLTExcludePortal, fail-closed hardcoded). In a
// community test build it does not fail as a missing TYPE but as a missing NAMESPACE
// (CS0234), which is a reminder that the gated surface is not only type names.
//
// The other 12 tests are profile-independent and stay in the origin fixture. See Gated/README.md.

using FluentAssertions;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests.Gated;

public class PortalCapacityInstanceKeyTests
{
    [Fact]
    public void A_ported_instance_inside_a_multi_instance_key_is_still_matched()
    {
        // The narrow live defect: with a comma-joined key, "A,B,1433" split to ["A","B","1433"],
        // so the real server "B,1433" matched no token and its capacity metric was dropped. The
        // exact-equality arm masked it whenever the key held only ONE instance, which is why it
        // survived. Both arms are exercised here.
        var key = CachingQueryExecutor.BuildInstanceKey(
            new DashboardFilter { Instances = new[] { "A", "B,1433" } });

        SQLTriage.Data.Services.Portal.CapacityCollector
            .InstanceKeyMatchesServer(key, "B,1433").Should().BeTrue(
                "the ported instance is in the key and must be recognised");
        SQLTriage.Data.Services.Portal.CapacityCollector
            .InstanceKeyMatchesServer(key, "A").Should().BeTrue();
        SQLTriage.Data.Services.Portal.CapacityCollector
            .InstanceKeyMatchesServer(key, "1433").Should().BeFalse(
                "a port must never match as though it were an instance");
        SQLTriage.Data.Services.Portal.CapacityCollector
            .InstanceKeyMatchesServer(key, "C").Should().BeFalse();
    }
}
