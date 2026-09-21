/* In the name of God, the Merciful, the Compassionate */

// Ruling 3 (DECISIONS 2026-08-26 04:20): POST/PUT/DELETE /api/v1/alerts/thresholds wrote
// AlertingService's alert-thresholds.json — a store read only by EvaluateAlerts, and EvaluateAlerts
// has no call site anywhere in the repo — so a threshold created through those routes never fired.
// An endpoint that silently does nothing is worse than none, so the three write routes were REMOVED
// (no in-repo or UI caller exists; grep proved it). The GET read stays: it is the authenticated
// read the RBAC handler census pins and it reports the stored thresholds honestly.
//
// This is a source lint, deliberately, in the same spirit as RbacHandlerGateCensusTests: it reads
// the shipped text a reviewer would read. RbacHandlerGateCensusTests independently proves the
// mapped-route surface equals its pinned table (from which those three rows were removed), so a
// route that came back would fail there too.

#nullable enable

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests
{
    public class AlertThresholdWriteApiRemovedTests
    {
        private static string Read(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RawPassedScan.RepoRoot().FullName }.Concat(parts).ToArray()));

        private static string Api() => Read("Data", "Services", "ApiEndpoints.cs");
        private static string Service() => Read("Data", "AlertingService.cs");

        [Fact]
        public void The_read_route_stays()
        {
            Assert.Matches(new Regex(@"MapGet\(\s*""/alerts/thresholds"""), Api());
        }

        [Theory]
        [InlineData("MapPost")]
        [InlineData("MapPut")]
        [InlineData("MapDelete")]
        public void The_dead_write_routes_are_gone(string verb)
        {
            Assert.DoesNotMatch(new Regex(verb + @"\(\s*""/alerts/thresholds"), Api());
        }

        /// <summary>
        /// The service mutators the removed routes reached are gone too — they had no other caller.
        /// GetThresholds (the GET read) and AddThreshold (the ConfigStoreWriteGuardTests vehicle for
        /// the shared config-store write guard) stay.
        /// </summary>
        [Fact]
        public void The_orphaned_threshold_mutators_are_removed_but_the_used_ones_stay()
        {
            var svc = Service();
            Assert.DoesNotContain("public void UpdateThreshold", svc);
            Assert.DoesNotContain("public void RemoveThreshold", svc);
            Assert.Contains("public List<AlertThreshold> GetThresholds", svc);
            Assert.Contains("public void AddThreshold", svc);
        }
    }
}
