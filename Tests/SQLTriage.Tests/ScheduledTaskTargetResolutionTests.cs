/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
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

            // Identical to the pre-dedupe loop for this case, because none of these three addresses
            // collide — the unpinned path's defect (below) only shows up when two connections declare
            // the same server, and this scenario never does.
            Assert.Equal(PreDedupeUnpinnedTargets(task, connections).Count, resolution.Targets.Count);
            Assert.Null(resolution.CollapsedDuplicates);
        }

        // ── (d) unpinned fan-out duplicates (2026-08-20 ruling: soft dedupe, never a naive Distinct) ──
        //
        // Confirmed first, per Adrian's ask: before this wave, the unpinned branch fanned out across
        // every enabled connection's own server list with NO dedup at all — a source comment even
        // framed it as deliberate ("estate-wide by definition"). Two connections declaring the same
        // server therefore produced two full assessments of one host, exactly the defect class the
        // PINNED branch above was fixed for on 2026-08-01, just never applied to the unpinned one.
        //
        // <see cref="PreDedupeUnpinnedTargets"/> is a verbatim transcription of that pre-ruling loop
        // (the exact body this wave replaces), kept for the same reason <see cref="PreFixTargets"/> is
        // kept above: the delta a reader is trusting this wave to have closed is stated in executable
        // form, not prose. Observed directly (2026-08-20, run against the code before the fix landed):
        // BOTH forms below — same exact server string, and an alias pair ("." vs this machine's own
        // name) — produced 2 targets. The assertions on <c>before</c> pin that observation; the
        // assertions on <c>ScheduledTaskEngine.ResolveTargets</c> pin the fix that replaced it.

        /// <summary>Verbatim transcription of the unpinned branch's pre-2026-08-20 body (no dedup).</summary>
        private static List<(ServerConnection Conn, string Server)> PreDedupeUnpinnedTargets(
            ScheduledTaskDefinition task, IReadOnlyList<ServerConnection> serverConnections)
        {
            var all = new List<(ServerConnection, string)>();
            foreach (var conn in serverConnections)
                foreach (var serverName in conn.GetServerList())
                    all.Add((conn, serverName));
            return all;
        }

        [Fact]
        public void UnpinnedTask_TwoConnectionsDeclareTheExactSameServerString_PreDedupeProducedTwo_NowCollapsesToOne()
        {
            var first = Conn(ServerA);
            var duplicate = Conn(ServerA);
            var connections = new List<ServerConnection> { first, duplicate };
            var task = Task(null);

            // Confirmed: the pre-ruling loop ran this server twice.
            var before = PreDedupeUnpinnedTargets(task, connections);
            Assert.Equal(2, before.Count);
            Assert.All(before, t => Assert.Equal(ServerA, t.Server));

            // Fixed: the same input now yields one target, owned by the FIRST enabled connection —
            // the same "first connection wins" semantics the pinned branch and the Report path use.
            var resolution = ScheduledTaskEngine.ResolveTargets(task, connections);
            Assert.Single(resolution.Targets);
            Assert.Same(first, resolution.Targets[0].Connection);
            Assert.Equal(ServerA, resolution.Targets[0].ServerName);
        }

        [Fact]
        public void UnpinnedTask_TwoConnectionsDeclareAliasFormsOfOneServer_PreDedupeProducedTwo_NowCollapsesToOne()
        {
            var dotConn = Conn(".");
            var machineConn = Conn(Environment.MachineName);
            var connections = new List<ServerConnection> { dotConn, machineConn };
            var task = Task(null);

            // Confirmed: the pre-ruling loop treated "." and this machine's own name as two distinct
            // servers, because it never ran IsSameServer at all on this branch.
            var before = PreDedupeUnpinnedTargets(task, connections);
            Assert.Equal(2, before.Count);

            // Fixed: they are recognised as the same server (local-host alias tier) and collapse to
            // one target, kept from the first connection with its OWN declared form (".").
            var resolution = ScheduledTaskEngine.ResolveTargets(task, connections);
            Assert.Single(resolution.Targets);
            Assert.Same(dotConn, resolution.Targets[0].Connection);
            Assert.Equal(".", resolution.Targets[0].ServerName);
        }

        [Fact]
        public void UnpinnedTask_DuplicateCollapse_RecordsBothConnectionsAndTheServerActuallyRun()
        {
            var kept = Conn(ServerA);
            var dropped = Conn(ServerA);
            var connections = new List<ServerConnection> { kept, dropped };

            var resolution = ScheduledTaskEngine.ResolveTargets(Task(null), connections);

            Assert.NotNull(resolution.CollapsedDuplicates);
            var dup = Assert.Single(resolution.CollapsedDuplicates!);
            Assert.Equal(ServerA, dup.ServerName);
            Assert.Same(kept, dup.KeptConnection);
            Assert.Same(dropped, dup.DroppedConnection);
        }

        [Fact]
        public void UnpinnedTask_ThreeConnections_OnlyTwoCollide_ThirdStaysDistinct()
        {
            var first = Conn(ServerA);
            var duplicate = Conn(ServerA);
            var distinct = Conn(ServerB);
            var connections = new List<ServerConnection> { first, duplicate, distinct };

            var resolution = ScheduledTaskEngine.ResolveTargets(Task(null), connections);

            Assert.Equal(2, resolution.Targets.Count);
            Assert.Equal(
                new[] { ServerA, ServerB },
                resolution.Targets.Select(t => t.ServerName).ToArray());
            Assert.Same(first, resolution.Targets[0].Connection);
            Assert.Same(distinct, resolution.Targets[1].Connection);

            var dup = Assert.Single(resolution.CollapsedDuplicates!);
            Assert.Equal(ServerA, dup.ServerName);
            Assert.Same(first, dup.KeptConnection);
            Assert.Same(duplicate, dup.DroppedConnection);
        }

        [Fact]
        public void UnpinnedTask_NoCollidingServers_CollapsedDuplicatesIsNullOrEmpty()
        {
            var connections = new List<ServerConnection> { Conn(ServerA), Conn(ServerB), Conn(ServerC) };

            var resolution = ScheduledTaskEngine.ResolveTargets(Task(null), connections);

            Assert.Equal(3, resolution.Targets.Count);
            Assert.True(resolution.CollapsedDuplicates == null || resolution.CollapsedDuplicates.Count == 0);
        }

        [Theory]
        [InlineData(AuthenticationTypes.Windows)]
        [InlineData(AuthenticationTypes.SqlServer)]
        [InlineData(AuthenticationTypes.EntraMFA)]
        public void DescribeCredentials_NamesTheAuthTypeAndLoginWithoutThePassword(string authType)
        {
            var conn = new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = ServerA,
                IsEnabled = true,
                AuthenticationType = authType,
                Username = "svc_account",
            };
            conn.SetPassword("a-secret-password-that-must-never-appear");

            var method = typeof(ScheduledTaskEngine).GetMethod(
                "DescribeCredentials", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            var description = (string)method!.Invoke(null, new object[] { conn })!;

            Assert.DoesNotContain("a-secret-password-that-must-never-appear", description);
            if (authType == AuthenticationTypes.Windows)
            {
                Assert.Equal("Windows Authentication", description);
            }
            else
            {
                Assert.Contains("svc_account", description);
            }
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

        // ── 4f: the Report task path (ExecuteReportTaskAsync) now derives its distinct,
        // estate-wide server list from ResolveTargets, the same alias policy every other task
        // type already runs through. ExecuteReportTaskAsync is private, so these tests pin the
        // resolution logic it now shares rather than the method itself. ──────────────────────

        /// <summary>The exact transform ExecuteReportTaskAsync applies to a TargetResolution to
        /// get its distinct, estate-wide server list (ScheduledTaskEngine.cs).</summary>
        private static List<string> ReportPathServers(ScheduledTaskEngine.TargetResolution resolution) =>
            resolution.Targets
                .Select(t => t.ServerName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

        [Fact]
        public void ReportTask_AliasPinned_ResolvesTheSameTargetAsAnAssessmentTask()
        {
            // Before 4f, the Report path exact-matched task.ServerName against each connection's
            // declared address and had no alias tier at all — an alias-pinned Report task
            // (machine-name pin against a "." connection, or vice versa) matched nothing and
            // silently rendered a report over zero servers, while an Assessment task pinned the
            // same way resolved fine. Both task types now call ResolveTargets, so they must agree.
            var owner = Conn(@".\OLD2017,56510");
            var connections = new List<ServerConnection> { Conn(ServerB), owner };
            var pinned = $@"{Environment.MachineName}\OLD2017,56510"; // alias for .\OLD2017,56510

            var assessmentTargets = ScheduledTaskEngine.ResolveTargets(
                new ScheduledTaskDefinition { Id = "t1", Name = "Nightly assessment", ServerName = pinned, TaskType = TaskType.Assessment },
                connections).Targets.Select(t => t.ServerName).ToList();

            var reportResolution = ScheduledTaskEngine.ResolveTargets(
                new ScheduledTaskDefinition { Id = "t2", Name = "Nightly report", ServerName = pinned, TaskType = TaskType.Report },
                connections);

            Assert.Equal(assessmentTargets, ReportPathServers(reportResolution));
            Assert.Equal(new[] { @".\OLD2017,56510" }, ReportPathServers(reportResolution));
            Assert.Null(reportResolution.UnownedPinnedServer);
        }

        [Fact]
        public void ReportTask_ResolutionWithNoDeclaredServers_YieldsZeroTargets()
        {
            // The exact scenario ExecuteReportTaskAsync's zero-target guard exists for:
            // ResolveTargets itself returns nothing to report over (every enabled connection
            // declares no servers). A PINNED report task can never reach this — ResolveTargets
            // always falls back to one target and sets UnownedPinnedServer instead; only the
            // unpinned, nothing-configured case yields truly zero.
            var connections = new List<ServerConnection> { Conn("") };

            var resolution = ScheduledTaskEngine.ResolveTargets(Task(null), connections);

            Assert.Empty(ReportPathServers(resolution));
        }

        // ── reports-r1-09: the unattended report named servers it never measured ──────────────
        //
        // ResolveTargets applies no seat filter, and CheckExecutionService.GetResults returns an
        // EMPTY list for an instance the licence does not cover. So the scope line was stamped from
        // the full target list while the findings came only from servers that produced rows.
        // Measured at the seam before the fix, with a one-seat register over three targets: "Estate
        // Report Executive Briefing 3 servers · SRV1, SRV2, SRV3  0% Passed  0 of 1 checks passed" —
        // the words "excluded" and "licence" appeared nowhere. GetResults' own contract
        // (CheckExecutionService) says callers that list servers must surface GetExcludedServers();
        // the CLI was the only consumer that did.
        //
        // The full ExecuteReportTaskAsync chain needs the whole scheduler DI, so these pin the
        // sentence the report now carries — AssessmentMeta.CoverageNote, which the Executive
        // Briefing prints on the cover beside the pass-rate donut and again on the summary page.

        [Fact]
        public void CoverageNote_NamesLicenceExcludedTargets_AndSaysNothingWasAssessedOnThem()
        {
            var note = ScheduledTaskEngine.BuildReportCoverageNote(
                targetCount: 3, contributedCount: 1,
                licenceExcluded: new[] { "SRV2", "SRV3" },
                noResults: Array.Empty<string>());

            Assert.Contains("1 of 3 target servers", note);
            Assert.Contains("Not covered by your licence", note);
            Assert.Contains("SRV2, SRV3", note);
            Assert.Contains("nothing was assessed on them", note);
        }

        [Fact]
        public void CoverageNote_SeparatesAnUnauditedServerFromAnUnlicensedOne()
        {
            // Two different facts about why a named server contributed nothing. A client reading a
            // scope line deserves to know which one applies to which server.
            var note = ScheduledTaskEngine.BuildReportCoverageNote(
                targetCount: 3, contributedCount: 1,
                licenceExcluded: new[] { "SRV2" },
                noResults: new[] { "SRV3" });

            Assert.Contains("Not covered by your licence, so nothing was assessed on them: SRV2.", note);
            Assert.Contains("No cached check results, so nothing was assessed on them: SRV3.", note);
        }

        [Fact]
        public void CoverageNote_IsEmpty_WhenEveryTargetContributed()
        {
            // An unaffected report renders exactly as it always has: no note, no new line on the cover.
            Assert.Equal(string.Empty, ScheduledTaskEngine.BuildReportCoverageNote(
                targetCount: 2, contributedCount: 2,
                licenceExcluded: Array.Empty<string>(), noResults: Array.Empty<string>()));
        }

        [Fact]
        public void CoverageNote_CountsTheRemainder_RatherThanDroppingNamesSilently()
        {
            var many = Enumerable.Range(1, 9).Select(i => $"SRV{i}").ToArray();

            var note = ScheduledTaskEngine.BuildReportCoverageNote(
                targetCount: 10, contributedCount: 1,
                licenceExcluded: many, noResults: Array.Empty<string>());

            Assert.Contains("SRV1, SRV2, SRV3, SRV4, SRV5, SRV6 and 3 more", note);
        }

        // ── reports-r1-09, fix round: ONE label for the measured scope ────────────────────────
        //
        // The first fix moved the cover subtitle onto the contributed set and left the page footer,
        // the delivered file name and the client email subject stamped from the target count. One
        // delivery therefore said "SRV1" on its cover, "3 servers" on every page footer, arrived as
        // ExecutiveBriefing_3_servers_….pdf, and was announced by an email subject reading
        // "SQLTriage Estate Report — 3 servers" with no coverage note in it at all.

        [Fact]
        public void ScopeLabel_NamesTheMeasuredSet_NotTheTargetCount()
        {
            Assert.Equal("SRV1", ScheduledTaskEngine.ScopeLabel(new[] { "SRV1" }, targetCount: 3));
            Assert.Equal("2 servers", ScheduledTaskEngine.ScopeLabel(new[] { "SRV1", "SRV2" }, targetCount: 5));
        }

        [Fact]
        public void ScopeLabel_WhenNothingWasMeasured_SaysSo_RatherThanNamingTheTargets()
        {
            // The fallback this replaces was `contributed.Count > 0 ? contributed : servers`, which
            // reinstated the exact over-claim the finding is about whenever nothing contributed.
            var label = ScheduledTaskEngine.ScopeLabel(Array.Empty<string>(), targetCount: 3);

            Assert.Equal("0 of 3 servers measured", label);
            Assert.DoesNotContain("SRV", label);

            var subtitle = ScheduledTaskEngine.ScopeSubtitle(Array.Empty<string>(), targetCount: 3);
            Assert.Equal("No data from any of the 3 target servers", subtitle);
        }

        [Fact]
        public void ScopeSubtitle_CountsTheRemainder_RatherThanDroppingNamesSilently()
        {
            var many = Enumerable.Range(1, 9).Select(i => $"SRV{i}").ToArray();

            var subtitle = ScheduledTaskEngine.ScopeSubtitle(many, targetCount: 9);

            Assert.StartsWith("9 servers · ", subtitle);
            Assert.Contains("and 3 more", subtitle);
        }

        /// <summary>
        /// ⚠ A LINT, NOT A BOUNDARY — it matches text in one method's source. ExecuteReportTaskAsync
        /// is private and its chain needs the whole scheduler DI, so the four label consumers cannot
        /// be driven from here; what CAN be checked is that they all read the one variable the
        /// measured set is assigned to. A rewrite that renames things and keeps the drift passes
        /// this, exactly as ExecutiveHealthScoreReaderLintTests says of itself. It is here because
        /// the drift it catches is a one-word edit and nothing else in the suite would see it.
        /// </summary>
        [Fact]
        public void ScheduledReportLabels_AllComeFromTheMeasuredScope_SourceLint()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "Data", "Services", "ScheduledTaskEngine.cs"));
            var start = source.IndexOf("private async Task ExecuteReportTaskAsync", StringComparison.Ordinal);
            Assert.True(start > 0, "ExecuteReportTaskAsync was renamed; re-point this lint");
            var end = source.IndexOf("internal static string BuildReportCoverageNote", start, StringComparison.Ordinal);
            Assert.True(end > start, "the method's end marker moved; re-point this lint");
            var body = source[start..end];

            // The measured set is what the label is built from…
            Assert.Contains("scopeLabel = ScopeLabel(contributed, servers.Count);", body);
            // …and every delivered label reads that variable rather than re-deriving from targets.
            Assert.Contains("FooterMeta   = $\"SQLTriage — Executive Briefing — {scopeLabel}", body);
            Assert.Contains("SaveToReportsFolder(pdf, scopeLabel", body);
            Assert.Contains("var subject = $\"SQLTriage Estate Report — {scopeLabel}", body);
            // …and the email carries the coverage note, which its subject line has no room for.
            Assert.Contains("HtmlEncode(coverageNote)", body);

            // The positive control: the pre-fix form is gone from this method entirely.
            Assert.DoesNotContain("servers.Count == 1 ? servers[0] : $\"{servers.Count} servers\"", body);
            Assert.DoesNotContain("contributed.Count > 0 ? contributed : servers", body);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Data", "Services", "ScheduledTaskEngine.cs")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                "Could not find the repo root above " + AppContext.BaseDirectory
                + ". This lint reads the source tree on purpose; if the suite is run somewhere "
                + "without sources it must FAIL rather than silently pass.");
        }
    }
}
