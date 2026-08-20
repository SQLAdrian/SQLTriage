/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Defect B (2026-08-01): a Daily task pinned to a single server ran once per ENABLED CONNECTION
    /// instead of once. Three enabled connections on this box produced three full assessments of "."
    /// at 02:01:51 / 02:02:42 / 02:03:50 and six blob PUTs to the production intake instead of two.
    /// These tests pin the resolution rule that replaced it.
    /// </summary>
    public class ScheduledTaskTargetResolutionTests
    {
        // Deliberately instance-qualified so they can never collide with Environment.MachineName,
        // which the local-alias tier treats as an alias for ".".
        private const string ServerA = @"SRVA\INST";
        private const string ServerB = @"SRVB\INST";
        private const string ServerC = @"SRVC\INST";

        private static ServerConnection Conn(string serverNames) =>
            new() { Id = Guid.NewGuid().ToString(), ServerNames = serverNames, IsEnabled = true };

        private static ScheduledTaskDefinition Task(string? pinnedServer) =>
            new()
            {
                Id = "task-1",
                Name = "Nightly assessment",
                ServerName = pinnedServer ?? string.Empty,
                TaskType = TaskType.Assessment
            };

        /// <summary>
        /// A VERBATIM transcription of the pre-fix loop body at main@791d2cb
        /// (Data/Services/ScheduledTaskEngine.cs:223-233), kept here so the delta this wave fixes is
        /// stated in executable form.
        ///
        /// <para>Honest label: this is a transcription, NOT the original code being invoked — the old
        /// loop was inline inside a private method with no seam. It is exact, and the test below shows
        /// it yields 3 targets for the same input where ResolveTargets yields 1.</para>
        /// </summary>
        private static List<(ServerConnection Conn, string Server)> PreFixTargets(
            ScheduledTaskDefinition task, IReadOnlyList<ServerConnection> serverConnections)
        {
            var targets = new List<(ServerConnection, string)>();
            foreach (var conn in serverConnections)
            {
                var servers = string.IsNullOrEmpty(task.ServerName)
                    ? conn.GetServerList()
                    : new List<string> { task.ServerName };

                foreach (var serverName in servers)
                    targets.Add((conn, serverName));
            }
            return targets;
        }

        // ── (a) a pinned name owned by one of three connections yields exactly ONE target ──

        [Fact]
        public void PinnedServer_OwnedByOneConnection_YieldsExactlyOneTarget()
        {
            var owner = Conn(ServerA);
            var connections = new List<ServerConnection> { owner, Conn(ServerB), Conn(ServerC) };
            var task = Task(ServerA);

            var resolution = ScheduledTaskEngine.ResolveTargets(task, connections);

            Assert.Single(resolution.Targets);
            Assert.Same(owner, resolution.Targets[0].Connection);
            Assert.Equal(ServerA, resolution.Targets[0].ServerName);
            Assert.Null(resolution.UnownedPinnedServer);
        }

        [Fact]
        public void PinnedServer_PreFixLoop_RanOncePerEnabledConnection()
        {
            var connections = new List<ServerConnection> { Conn(ServerA), Conn(ServerB), Conn(ServerC) };
            var task = Task(ServerA);

            // The behaviour observed live: the multiplier is exactly the enabled-connection count,
            // and every run after the first carried a DIFFERENT connection's credential settings.
            var before = PreFixTargets(task, connections);
            Assert.Equal(3, before.Count);
            Assert.All(before, t => Assert.Equal(ServerA, t.Server));
            Assert.Equal(3, before.Select(t => t.Conn.Id).Distinct().Count());

            Assert.Single(ScheduledTaskEngine.ResolveTargets(task, connections).Targets);
        }

        // ── (b) a pinned name owned by NO enabled connection yields ONE target, never ZERO ──

        [Fact]
        public void PinnedServer_OwnedByNoConnection_FallsBackToFirst_NeverZeroTargets()
        {
            var first = Conn(ServerA);
            var connections = new List<ServerConnection> { first, Conn(ServerB), Conn(ServerC) };
            var task = Task(@"GHOST\NOT-CONFIGURED");

            var resolution = ScheduledTaskEngine.ResolveTargets(task, connections);

            // Zero targets would silently stop the task forever and leave the portal stale with
            // nothing in the log to explain it — strictly worse than the over-run being fixed.
            Assert.Single(resolution.Targets);
            Assert.Same(first, resolution.Targets[0].Connection);
            Assert.Equal(@"GHOST\NOT-CONFIGURED", resolution.Targets[0].ServerName);

            // …and the caller is told to warn, so the misconfiguration is visible rather than silent.
            Assert.Equal(@"GHOST\NOT-CONFIGURED", resolution.UnownedPinnedServer);
        }

        // ── (c) an unpinned task still fans out across every connection, as before ──

        [Fact]
        public void UnpinnedTask_StillFansOutAcrossEveryConnection()
        {
            var multi = Conn($"{ServerA}\n{ServerB}");
            var single = Conn(ServerC);
            var connections = new List<ServerConnection> { multi, single };
            var task = Task(null);

            var resolution = ScheduledTaskEngine.ResolveTargets(task, connections);

            Assert.Equal(3, resolution.Targets.Count);
            Assert.Null(resolution.UnownedPinnedServer);
            Assert.Equal(
                new[] { ServerA, ServerB, ServerC },
                resolution.Targets.Select(t => t.ServerName).ToArray());
            Assert.Same(multi, resolution.Targets[0].Connection);
            Assert.Same(multi, resolution.Targets[1].Connection);
            Assert.Same(single, resolution.Targets[2].Connection);

            // Identical to the pre-fix loop for this case — the unpinned path was never the defect.
            Assert.Equal(PreFixTargets(task, connections).Count, resolution.Targets.Count);
        }

        [Fact]
        public void UnpinnedTask_WhitespaceServerName_IsTreatedAsUnpinned()
        {
            var connections = new List<ServerConnection> { Conn(ServerA), Conn(ServerB) };

            var resolution = ScheduledTaskEngine.ResolveTargets(Task("   "), connections);

            Assert.Equal(2, resolution.Targets.Count);
            Assert.Null(resolution.UnownedPinnedServer);
        }

        [Fact]
        public void NoEnabledConnections_YieldsNoTargets()
        {
            var resolution = ScheduledTaskEngine.ResolveTargets(Task(ServerA), new List<ServerConnection>());
            Assert.Empty(resolution.Targets);
            Assert.Null(resolution.UnownedPinnedServer);
        }

        [Fact]
        public void SameServerDeclaredByTwoConnections_StillYieldsOneTarget()
        {
            var firstOwner = Conn(ServerA);
            var duplicate = Conn(ServerA);
            var connections = new List<ServerConnection> { firstOwner, duplicate };

            var resolution = ScheduledTaskEngine.ResolveTargets(Task(ServerA), connections);

            // A duplicate configuration is not a reason to assess the same host twice.
            Assert.Single(resolution.Targets);
            Assert.Same(firstOwner, resolution.Targets[0].Connection);
        }

        // ── the live case: task pinned to ".", connection 1 declares "." ──

        [Fact]
        public void LiveShapedConfig_PinnedDot_ResolvesToTheConnectionThatDeclaresIt()
        {
            var dotConn = Conn(".");
            var connections = new List<ServerConnection> { dotConn, Conn(@".\OLD2017,56510"), Conn(@".\NEW2022,50644") };

            var resolution = ScheduledTaskEngine.ResolveTargets(Task("."), connections);

            Assert.Single(resolution.Targets);
            Assert.Same(dotConn, resolution.Targets[0].Connection);
            Assert.Equal(".", resolution.Targets[0].ServerName);
            Assert.Null(resolution.UnownedPinnedServer);
        }

        // ── alias policy ────────────────────────────────────────────────

        [Theory]
        [InlineData(".", "localhost")]
        [InlineData(".", "(local)")]
        [InlineData(".", "127.0.0.1")]
        [InlineData("LOCALHOST", ".")]
        [InlineData(@".\OLD2017", @"localhost\OLD2017")]
        [InlineData(@"localhost\OLD2017,56510", @".\OLD2017,56510")]
        [InlineData(@"SRVA\INST", @"srva\inst")]
        public void IsSameServer_TreatsLocalHostAliasesAsOneServer(string declared, string pinned)
        {
            Assert.True(ScheduledTaskEngine.IsSameServer(declared, pinned));
        }

        [Fact]
        public void IsSameServer_MachineNameIsAnAliasForDot()
        {
            var machine = Environment.MachineName;
            Assert.True(ScheduledTaskEngine.IsSameServer(machine, "."));
            Assert.True(ScheduledTaskEngine.IsSameServer($@"{machine}\OLD2017", @".\OLD2017"));
        }

        [Theory]
        [InlineData("SRVA", @"SRVA\INST")]          // a bare host is NOT the named instance on it
        [InlineData(@"SRVA\INST1", @"SRVA\INST2")]  // different instances on one host
        [InlineData(".", @".\OLD2017")]             // default instance is not the named instance
        [InlineData(@".\OLD2017", @".\OLD2017,56510")] // port is significant
        [InlineData("SRVA", "SRVB")]
        [InlineData(".", "")]
        [InlineData("", ".")]
        public void IsSameServer_KeepsInstanceAndPortSignificant(string declared, string pinned)
        {
            Assert.False(ScheduledTaskEngine.IsSameServer(declared, pinned));
        }

        [Fact]
        public void ResolvedTarget_UsesTheOwningConnectionsDeclaredAddress_SoThePortSurvives()
        {
            // The declared form carries the port the connection actually dials; the pinned alias
            // may not. Everything else in the estate keys results on the declared address.
            var owner = Conn(@".\OLD2017,56510");
            var connections = new List<ServerConnection> { Conn(ServerB), owner };
            var task = Task($@"{Environment.MachineName}\OLD2017,56510");

            var resolution = ScheduledTaskEngine.ResolveTargets(task, connections);

            Assert.Single(resolution.Targets);
            Assert.Same(owner, resolution.Targets[0].Connection);
            Assert.Equal(@".\OLD2017,56510", resolution.Targets[0].ServerName);
            Assert.Null(resolution.UnownedPinnedServer);
        }
    }
}
