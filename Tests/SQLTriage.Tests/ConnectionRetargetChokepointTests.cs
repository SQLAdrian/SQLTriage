/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:ConnectionRetargetChokepointTests — the edge that survived three rounds of gating
    /// <summary>
    /// The process-wide connection edge — <c>ServerConnectionManager.CurrentServer</c> — exercised
    /// rather than read.
    ///
    /// <para><b>Why this class exists at all.</b> The edge was closed three times. Round 1 gated the
    /// two selector handlers. Round 2 found the init path open and gated
    /// <c>DynamicDashboard.SetServerContext</c>, calling it "the single point all five call sites
    /// pass through". Round 3 found a SIXTH write sixty lines below that claim, inside
    /// <c>DiscoverAndUpdateInstancesAsync</c>, ungated, reached by every dashboard load. Each round
    /// gated the sites it could see and shipped the next one, and every instrument pointed at the
    /// problem was a source scanner reading the method it had been told was the chokepoint.</para>
    ///
    /// <para><b>⚠ ONE CONNECTION HIDES THIS DEFECT.</b> The discovery loop set the current server to
    /// each enabled connection in turn. On an install with a single enabled connection it wrote the
    /// same id back and left no trace at all; a probe with one connection PASSES with the defect
    /// live. That is why every behavioural test below configures TWO enabled connections — a
    /// reachable <c>.</c> and an unreachable <c>GATEPROBE-NOSUCHHOST</c> — and asserts on which one
    /// the process is left pointing at.</para>
    ///
    /// <para><b>Nothing here reaches SQL Server.</b> The probe is substituted, so the unreachable
    /// name is unreachable by construction rather than by network timeout, and the suite stays
    /// deterministic on a box with no instances.</para>
    /// </summary>
    public class ConnectionRetargetChokepointTests : IDisposable
    {
        private readonly string _tempDir;

        public ConnectionRetargetChokepointTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "retarget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        // ── The estate: two enabled connections, one reachable, one not ──

        private const string Reachable = ".";
        private const string Unreachable = "GATEPROBE-NOSUCHHOST";

        /// <summary>
        /// A manager over a TEMP store (never the install's Config/server-connections.json) holding
        /// the two enabled connections this whole file turns on.
        /// </summary>
        private ServerConnectionManager TwoEnabledConnections(
            out ServerConnection reachable, out ServerConnection unreachable)
        {
            var manager = new ServerConnectionManager(
                NullLogger<ServerConnectionManager>.Instance,
                seats: null,
                connectionsFilePath: Path.Combine(_tempDir, "server-connections.json"));

            reachable = new ServerConnection
            {
                ServerNames = Reachable,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                IsEnabled = true,
            };
            unreachable = new ServerConnection
            {
                ServerNames = Unreachable,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                IsEnabled = true,
            };

            Assert.True(manager.AddConnection(reachable).Succeeded);
            Assert.True(manager.AddConnection(unreachable).Succeeded);

            // The precondition the defect needed and a one-connection probe cannot provide.
            Assert.Equal(2, manager.GetEnabledConnections().Count);
            return manager;
        }

        // ── The caller: an anonymous loopback viewer ─────────────────────

        /// <summary>
        /// RBAC as it is on a configured install: enabled, one enabled Windows admin, Windows auth
        /// on. Enforcement matters — <see cref="RbacService.IsAuthorized(string, string, AppUserState.BootstrapEligibilityProof)"/>
        /// only consults the permission matrix once RBAC is enforced, and on an UNCONFIGURED
        /// install a loopback caller is bootstrap-eligible and therefore authorised for everything.
        /// A "viewer" that is really an admin would make every assertion below vacuous.
        /// </summary>
        private RbacService EnforcedRbac()
        {
            var configPath = Path.Combine(_tempDir, "rbac-config.json");
            var usersPath = Path.Combine(_tempDir, "rbac-users.json");

            File.WriteAllText(configPath, JsonSerializer.Serialize(new RbacConfig
            {
                Enabled = true,
                Windows = new WindowsAuthConfig { Enabled = true },
            }));
            File.WriteAllText(usersPath, JsonSerializer.Serialize(new List<RbacUser>
            {
                new()
                {
                    Email = @"GATE\admin",
                    DisplayName = "Configured Admin",
                    Provider = AuthProviders.Windows,
                    Role = AppRoles.Admin,
                    Enabled = true,
                },
            }));

            var rbac = new RbacService(NullLogger<RbacService>.Instance, configPath, usersPath);
            Assert.True(rbac.IsRbacEnforced());
            return rbac;
        }

        /// <summary>
        /// The caller the acceptance criteria name: nobody signed in, arriving over loopback, on a
        /// browser-hosted install with RBAC enforced.
        /// </summary>
        private AppUserState AnonymousLoopbackViewer(RbacService rbac)
        {
            // No AuthenticationStateProvider in the container — the no-signal path, which must fail
            // CLOSED. That is exactly an anonymous caller.
            var state = new AppUserState(
                HostEnvironmentInfo.BrowserHosted,
                rbac,
                new ServiceCollection().BuildServiceProvider(),
                NullLogger<AppUserState>.Instance);

            Assert.Equal(AppRoles.Viewer, state.Role);

            // The loopback half, stated as the thing that MATTERS about it: the bootstrap hatch
            // does not rescue this caller, because enforcement ignores bootstrap eligibility. If
            // that ever changes, this assertion fails before any of the ones below go vacuous.
            // The hatch term is a capability token since 2026-08-17, and no caller can write one —
            // BootstrapProofForTests mints by reflection, which is the only route left and is
            // documented there. Forcing it ON is the point of these two lines.
            Assert.False(rbac.IsAuthorized(AppRoles.Viewer, "run_scripts", BootstrapProofForTests.Minted));
            Assert.False(rbac.IsAuthorized(AppRoles.Viewer, "execute_checks", BootstrapProofForTests.Minted));

            Assert.False(state.IsAuthorized("run_scripts"));
            Assert.False(state.IsAuthorized("execute_checks"));
            return state;
        }

        // ══ 1. THE DISCOVERY PASS — the fifth write, run rather than read ══

        /// <summary>
        /// THE ACCEPTANCE TEST for the round-3 defect.
        ///
        /// <para>The dashboard's discovery pass walked the process-wide current server across every
        /// enabled connection, ungated, on every load. It is now
        /// <see cref="SqlWatchInstanceDiscovery"/>, which is handed the connection to probe as an
        /// ARGUMENT — so the assertion is not "the write is gated" but the stronger "there is no
        /// write": reading which instances exist is not an act on any of them, and there is no role
        /// for which dragging every open screen across the estate would be correct.</para>
        ///
        /// <para>The probe records what the process was pointing at each time it was called, which
        /// is how this test would have caught the original: under the old loop the recording reads
        /// [reachable, unreachable] and the connection is left on whichever the loop ended on.</para>
        /// </summary>
        [Fact]
        public async Task DiscoveryNeverMovesTheProcessWideConnection_withTwoEnabledConnections()
        {
            var manager = TwoEnabledConnections(out var reachable, out var unreachable);
            var rbac = EnforcedRbac();
            var viewer = AnonymousLoopbackViewer(rbac);

            // Somebody authorised has already connected the process to the reachable instance.
            // This is the state the defect destroyed and a single-connection install cannot show.
            var admin = new ServerConnectionManagerAdmin(manager);
            admin.PointAt(reachable.Id);
            Assert.Equal(reachable.Id, manager.CurrentServer?.Id);

            var seenDuringProbe = new List<string?>();
            var probedNames = new List<string>();

            var discovery = new SqlWatchInstanceDiscovery(manager);
            var result = await discovery.DiscoverAsync((conn, ct) =>
            {
                seenDuringProbe.Add(manager.CurrentServer?.Id);
                probedNames.Add(conn.GetServerList().First());

                // The unreachable one answers the way an unreachable one answers: nothing.
                return Task.FromResult(conn.Id == reachable.Id
                    ? new List<string> { "SQLW-01" }
                    : new List<string>());
            });

            // ── The defect, stated as an assertion ──
            Assert.All(seenDuringProbe, seen => Assert.Equal(reachable.Id, seen));
            Assert.Equal(reachable.Id, manager.CurrentServer?.Id);

            // …and the viewer never became able to move it either way.
            Assert.False(viewer.IsAuthorized("run_scripts"));

            // The pass still did its job: each connection probed once, by name, and the discovered
            // SQLWATCH name mapped back to the connection that answered.
            Assert.Equal(new[] { Reachable, Unreachable }, probedNames);
            Assert.Equal(reachable.Id, result.InstanceToConnectionId["SQLW-01"]);
            Assert.Contains(reachable.Id, result.ConnectionsWithSqlWatch);
            Assert.DoesNotContain(unreachable.Id, result.ConnectionsWithSqlWatch);

            // The dropdown holds the discovered name for the one that answered and the configured
            // name for the one that did not — unchanged behaviour, now without the singleton.
            Assert.Equal(new[] { "SQLW-01", Unreachable }, result.Instances);
        }

        /// <summary>
        /// A probe that throws — an unreachable host, in the shape the real one arrives in — must
        /// not abandon the pass or move the connection. The old loop's try/catch is preserved.
        /// </summary>
        [Fact]
        public async Task AThrowingProbeNeitherStopsDiscoveryNorMovesTheConnection()
        {
            var manager = TwoEnabledConnections(out var reachable, out var unreachable);
            new ServerConnectionManagerAdmin(manager).PointAt(reachable.Id);

            var discovery = new SqlWatchInstanceDiscovery(manager);
            var result = await discovery.DiscoverAsync((conn, ct) =>
                conn.Id == unreachable.Id
                    ? throw new InvalidOperationException("A network-related or instance-specific error…")
                    : Task.FromResult(new List<string> { "SQLW-01" }));

            Assert.Equal(reachable.Id, manager.CurrentServer?.Id);
            Assert.Equal(new[] { "SQLW-01", Unreachable }, result.Instances);
        }

        // ══ 2. THE MANAGER — the chokepoint itself ═══════════════════════

        /// <summary>
        /// A caller with no grant cannot move the connection, and a grant taken from an anonymous
        /// loopback viewer is no grant. Two connections, so a refusal is distinguishable from a
        /// write of the same value.
        /// </summary>
        [Fact]
        public void AnAnonymousLoopbackViewerCannotMoveTheConnection()
        {
            var manager = TwoEnabledConnections(out var reachable, out var unreachable);
            var viewer = AnonymousLoopbackViewer(EnforcedRbac());
            new ServerConnectionManagerAdmin(manager).PointAt(reachable.Id);

            foreach (var permission in new[] { "run_scripts", "execute_checks" })
            {
                var outcome = manager.SetCurrentServer(
                    unreachable.Id, ConnectionRetargetGrant.ForCaller(viewer, permission));

                Assert.False(outcome.Applied);
                Assert.False(outcome.Moved);
                Assert.NotNull(outcome.RefusedBecause);
                Assert.Contains(permission, outcome.RefusedBecause!, StringComparison.Ordinal);
                Assert.Equal(reachable.Id, manager.CurrentServer?.Id);
            }
        }

        /// <summary>
        /// <c>default(ConnectionRetargetGrant)</c> is a refusal. This is the property that makes the
        /// signature a chokepoint rather than a convention: forgetting to decide fails closed.
        /// </summary>
        [Fact]
        public void ADefaultGrantIsARefusal()
        {
            var manager = TwoEnabledConnections(out var reachable, out var unreachable);
            new ServerConnectionManagerAdmin(manager).PointAt(reachable.Id);

            var outcome = manager.SetCurrentServer(unreachable.Id, default);

            Assert.False(outcome.Applied);
            Assert.Equal(reachable.Id, manager.CurrentServer?.Id);
        }

        /// <summary>
        /// A null user is a refusal too — a call site with nobody to ask has not established that
        /// anybody authorised it.
        /// </summary>
        [Fact]
        public void AGrantWithNoUserToAskIsARefusal()
        {
            var manager = TwoEnabledConnections(out var reachable, out var unreachable);
            new ServerConnectionManagerAdmin(manager).PointAt(reachable.Id);

            var outcome = manager.SetCurrentServer(
                unreachable.Id, ConnectionRetargetGrant.ForCaller(null, "run_scripts"));

            Assert.False(outcome.Applied);
            Assert.Equal(reachable.Id, manager.CurrentServer?.Id);
        }

        /// <summary>
        /// The bootstrap, which is where the review note used to live: "it can only move the target
        /// from none to the first enabled connection." That was an assertion about two components;
        /// it is now a condition this manager checks.
        ///
        /// <para>⚠ Both halves need TWO connections to mean anything. With one, "establish" and
        /// "move" write the same id and the second assertion cannot fail.</para>
        /// </summary>
        [Fact]
        public void EstablishSetsTheFirstConnectionAndThenCannotMoveIt()
        {
            var manager = TwoEnabledConnections(out var reachable, out var unreachable);
            Assert.Null(manager.CurrentServer);

            // From "none" — allowed, and it is the establish the shell needs on a cold process.
            var first = manager.SetCurrentServer(reachable.Id, ConnectionRetargetGrant.Establish);
            Assert.True(first.Applied);
            Assert.True(first.Moved);
            Assert.Null(first.PreviousServerId);
            Assert.Equal(reachable.Id, manager.CurrentServer?.Id);

            // From one server to another — refused. This is the second browser tab whose own scoped
            // state is empty while the process is already connected, and it is the case the old
            // ungated bootstrap got wrong on every install with more than one connection.
            var second = manager.SetCurrentServer(unreachable.Id, ConnectionRetargetGrant.Establish);
            Assert.False(second.Applied);
            Assert.NotNull(second.RefusedBecause);
            Assert.Equal(reachable.Id, manager.CurrentServer?.Id);
        }

        /// <summary>
        /// An authorised caller still gets the write, and <c>Moved</c> distinguishes a real
        /// retarget from setting the same server back — the distinction any sentence that says
        /// "the connection was not changed" has to rest on.
        /// </summary>
        [Fact]
        public void AnAuthorisedCallerRetargetsAndTheOutcomeSaysWhetherItMoved()
        {
            var manager = TwoEnabledConnections(out var reachable, out var unreachable);
            var admin = AdminUserState();
            new ServerConnectionManagerAdmin(manager).PointAt(reachable.Id);

            var grant = ConnectionRetargetGrant.ForCaller(admin, "run_scripts");

            var moved = manager.SetCurrentServer(unreachable.Id, grant);
            Assert.True(moved.Applied);
            Assert.True(moved.Moved);
            Assert.Equal(reachable.Id, moved.PreviousServerId);
            Assert.Equal(unreachable.Id, manager.CurrentServer?.Id);

            var same = manager.SetCurrentServer(unreachable.Id, grant);
            Assert.True(same.Applied);
            Assert.False(same.Moved);
        }

        // ══ 3. THE TOP-BAR FRONT DOOR ════════════════════════════════════

        /// <summary>
        /// <see cref="ServerContextService"/> is AddScoped and its <c>CurrentServerId</c> is what
        /// the top-bar picker RENDERS, while the connection the queries use is the manager's
        /// singleton. On a refusal it must not record the request — a control showing a server the
        /// application is not connected to states the falsehood in the control instead of in the
        /// text, which is the same defect class as the notice this wave rewrote.
        /// </summary>
        [Fact]
        public async Task ARefusedTopBarSelectionAdoptsWhatHoldsRatherThanWhatWasAsked()
        {
            var manager = TwoEnabledConnections(out var reachable, out var unreachable);
            var viewer = AnonymousLoopbackViewer(EnforcedRbac());
            new ServerConnectionManagerAdmin(manager).PointAt(reachable.Id);

            var context = new ServerContextService(manager);
            var outcome = await context.SetServerAsync(
                unreachable.Id, ConnectionRetargetGrant.ForCaller(viewer, "execute_checks"));

            Assert.False(outcome.Applied);
            Assert.Equal(reachable.Id, manager.CurrentServer?.Id);
            Assert.Equal(reachable.Id, context.CurrentServerId);
        }

        /// <summary>
        /// The second tab. Its own scoped <c>CurrentServerId</c> is empty, which is what the
        /// bootstrap used to test, so it fired and moved the process onto its own first connection.
        /// </summary>
        [Fact]
        public async Task ASecondCircuitsBootstrapDoesNotDragTheProcessOntoItsOwnFirstConnection()
        {
            var manager = TwoEnabledConnections(out var reachable, out var unreachable);
            new ServerConnectionManagerAdmin(manager).PointAt(unreachable.Id);

            var freshTab = new ServerContextService(manager);
            Assert.Null(freshTab.CurrentServerId); // the empty test the old bootstrap trusted

            await freshTab.SetServerAsync(reachable.Id, ConnectionRetargetGrant.Establish);

            Assert.Equal(unreachable.Id, manager.CurrentServer?.Id);
            Assert.Equal(unreachable.Id, freshTab.CurrentServerId);
        }

        // ══ 4. THE CATEGORY — every write in the tree ════════════════════

        /// <summary>
        /// The census the previous two rounds each needed and did not have: EVERY call of the edge
        /// in the repository, not the ones in the file somebody was looking at.
        ///
        /// <para>The compiler already enforces this — the method takes two parameters — so this test
        /// cannot fail while the tree builds. It is kept because it is the thing a reader looks for
        /// after being burned twice, and because it FAILS LOUDLY rather than silently if the
        /// signature is ever relaxed back to a one-argument overload for convenience.</para>
        /// </summary>
        [Fact]
        public void EveryWriteOfTheConnectionEdgeInTheTreeCarriesAGrant()
        {
            var root = RawPassedScan.RepoRoot();
            var offenders = new List<string>();
            var seen = 0;

            foreach (var file in SourceFiles(root.FullName))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, @"\.SetCurrentServer\(([^;]*?)\)\s*;"))
                {
                    seen++;
                    var args = m.Groups[1].Value;
                    if (!args.Contains("grant", StringComparison.OrdinalIgnoreCase)
                        && !args.Contains("Grant", StringComparison.Ordinal)
                        && !args.Contains("default", StringComparison.Ordinal))
                    {
                        offenders.Add(Rel(root.FullName, file) + ": " + m.Value.Trim());
                    }
                }
            }

            Assert.True(seen > 0, "No SetCurrentServer call sites found — this census has gone blind.");
            Assert.True(offenders.Count == 0,
                "These writes of the process-wide connection do not name a decision. The edge has "
                + "shipped an ungated write three rounds running; a call site that does not say who "
                + "is asking is the shape every one of them had:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// And the interface still demands it. If somebody adds a one-argument convenience overload
        /// the census above starts passing vacuously, so the shape of the API is pinned too.
        /// </summary>
        [Fact]
        public void TheInterfaceOffersNoUngatedOverload()
        {
            var writes = typeof(IServerConnectionManager)
                .GetMethods()
                .Where(m => m.Name == "SetCurrentServer")
                .ToList();

            Assert.Single(writes);
            var parameters = writes[0].GetParameters();
            Assert.Equal(2, parameters.Length);
            Assert.Equal(typeof(ConnectionRetargetGrant), parameters[1].ParameterType);
            Assert.False(parameters[1].IsOptional,
                "An optional grant is not a chokepoint: every existing call site would compile "
                + "unchanged and the decision would go back to being something a call site may skip.");
        }

        // ══ 5. THE SECOND EDGE — the one that outranks the first ═════════
        //
        // Sections 1–4 close ServerConnectionManager._currentServerId and section 4's header calls
        // that "the category". It was not the category, it was one member of it. The category is
        // "process-wide state that decides which SQL Server this process's queries run against",
        // and the way to enumerate it is to read the resolver every query that asks for the AMBIENT
        // target goes through (not every query in the process — ~95 sites build a connection from a
        // caller-supplied string and never reach it) —
        // SqlServerConnectionFactory.GetCurrentConnectionString — and list what it consults. It
        // consults GlobalInstanceSelector.SelectedInstance FIRST. That member had three writers,
        // two of them ungated, on Pages/Sessions.razor: a routable page with no page-level gate,
        // on no census list in the tree, skipped by the component census because it is routable,
        // and unmatchable by the page census's verb lexicon because "Set" is not in it.

        /// <summary>
        /// The precedence, EXERCISED rather than quoted. One write of the instance edge moves what
        /// the factory hands out while the chokepointed edge is untouched and still points
        /// elsewhere — which is why gating only that one gated nothing at the point of use.
        ///
        /// <para>No SQL Server is contacted: <c>CreateConnection()</c> constructs a
        /// <c>SqlConnection</c> and does not open it, so the resolved Data Source can be read off
        /// the object on a box with no instances.</para>
        /// </summary>
        [Fact]
        public void TheSelectedInstanceOutranksTheCurrentServerAtThePointOfUse()
        {
            var manager = TwoEnabledConnections(out var reachable, out _);
            new ServerConnectionManagerAdmin(manager).PointAt(reachable.Id);

            var selector = new GlobalInstanceSelector(NullLogger<GlobalInstanceSelector>.Instance);
            var factory = new SqlServerConnectionFactory(
                manager, selector, "Server=FALLBACK-NEVER;Integrated Security=true;");

            Assert.Equal(Reachable, DataSourceOf(factory));

            Assert.True(selector.SetSelectedInstance(
                Unreachable, ConnectionRetargetGrant.ForCaller(AdminUserState(), "run_scripts")));

            Assert.Equal(reachable.Id, manager.CurrentServer?.Id);   // edge 1 never moved…
            Assert.Equal(Unreachable, DataSourceOf(factory));        // …and no longer decides
        }

        /// <summary>
        /// <c>default(ConnectionRetargetGrant)</c> is a refusal on this edge too, and a refusal
        /// notifies nobody — a subscriber told the instance changed would retarget its own queries
        /// on the strength of a write that was rejected.
        /// </summary>
        [Fact]
        public void ADefaultGrantCannotMoveTheSelectedInstance()
        {
            var selector = new GlobalInstanceSelector(NullLogger<GlobalInstanceSelector>.Instance);
            var announced = new List<string>();
            selector.OnInstanceChanged += announced.Add;

            Assert.False(selector.SetSelectedInstance(Unreachable, default));

            Assert.Null(selector.SelectedInstance);
            Assert.Empty(announced);
        }

        /// <summary>
        /// The caller the two Sessions.razor writers would have handed the process to. Both were
        /// unreachable from the shipped markup on 2026-08-07 — one markup line away from live.
        /// </summary>
        [Fact]
        public void AnAnonymousLoopbackViewerCannotMoveTheSelectedInstance()
        {
            var viewer = AnonymousLoopbackViewer(EnforcedRbac());
            var selector = new GlobalInstanceSelector(NullLogger<GlobalInstanceSelector>.Instance);

            Assert.False(selector.SetSelectedInstance(
                Unreachable, ConnectionRetargetGrant.ForCaller(viewer, "run_scripts")));

            Assert.Null(selector.SelectedInstance);
        }

        /// <summary>
        /// An establish is REFUSED here, not honoured. Nothing bootstraps this value — every reader
        /// treats "none selected" as "fall through to the current server", which has a bootstrap of
        /// its own — so a caller asking to establish one has not been thought about.
        /// </summary>
        [Fact]
        public void AnEstablishGrantIsRefusedOnTheInstanceEdge()
        {
            var selector = new GlobalInstanceSelector(NullLogger<GlobalInstanceSelector>.Instance);

            Assert.False(selector.SetSelectedInstance(Unreachable, ConnectionRetargetGrant.Establish));

            Assert.Null(selector.SelectedInstance);
        }

        /// <summary>
        /// Every write of the SECOND edge in the tree, by the same rule as
        /// <see cref="EveryWriteOfTheConnectionEdgeInTheTreeCarriesAGrant"/>.
        ///
        /// <para>Matched on a balanced-ish argument list rather than "up to the next semicolon",
        /// because the gated call sites sit inside an <c>if (!…)</c> and a semicolon-terminated
        /// window runs past the closing paren into whatever follows — a window that reads text the
        /// call does not contain can pass on evidence belonging to something else.</para>
        /// </summary>
        [Fact]
        public void EveryWriteOfTheInstanceEdgeInTheTreeCarriesAGrant()
        {
            var root = RawPassedScan.RepoRoot();
            var offenders = new List<string>();
            var seen = 0;

            foreach (var file in SourceFiles(root.FullName))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(
                             text, @"\.SetSelectedInstance\(([^()]*(?:\([^()]*\)[^()]*)*)\)"))
                {
                    seen++;
                    var args = m.Groups[1].Value;
                    if (!args.Contains("grant", StringComparison.OrdinalIgnoreCase)
                        && !args.Contains("default", StringComparison.Ordinal))
                        offenders.Add(Rel(root.FullName, file) + ": " + m.Value.Trim());
                }
            }

            Assert.True(seen > 0,
                "No SetSelectedInstance call sites found — this census has gone blind.");
            Assert.True(offenders.Count == 0,
                "These writes of the instance edge do not name a decision. This edge decides the "
                + "connection BEFORE the one sections 1-4 guard, so an ungated write here defeats "
                + "all of them:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// And the API still demands it, so the scan above cannot start passing vacuously against a
        /// convenience overload.
        /// </summary>
        [Fact]
        public void TheInstanceSelectorOffersNoUngatedOverload()
        {
            var writes = typeof(GlobalInstanceSelector)
                .GetMethods()
                .Where(m => m.Name == "SetSelectedInstance")
                .ToList();

            Assert.Single(writes);
            var parameters = writes[0].GetParameters();
            Assert.Equal(2, parameters.Length);
            Assert.Equal(typeof(ConnectionRetargetGrant), parameters[1].ParameterType);
            Assert.False(parameters[1].IsOptional,
                "An optional grant is not a chokepoint: every existing call site would compile "
                + "unchanged and the decision would go back to being something a call site may skip.");
        }

        /// <summary>
        /// The tripwire on the CATEGORY, which is the instrument every earlier round was missing.
        ///
        /// <para>Both rounds before this one enumerated members: call sites first, then one
        /// singleton. What decides the answer is what
        /// <c>SqlServerConnectionFactory.GetCurrentConnectionString</c> reads, so THAT is the list
        /// to hold still. Today it reads exactly three fields — the instance selector, the
        /// connection manager, and the encrypted fallback — and the first two are the two edges
        /// this file guards. A fourth determinant added to that method is a third edge, and this
        /// fails naming it rather than letting the next round discover it in a render.</para>
        ///
        /// <para>It is a source pin and it is a LINT, not a boundary: the boundary is the grant
        /// parameter the compiler enforces. Its job is to make a silent addition loud.</para>
        /// </summary>
        [Fact]
        public void TheConnectionResolverConsultsOnlyTheDeterminantsThisFileGuards()
        {
            var root = RawPassedScan.RepoRoot();
            var text = File.ReadAllText(
                Path.Combine(root.FullName, "Data", "SqlServerConnectionFactory.cs"));

            var body = MethodBody(text, "private string GetCurrentConnectionString()");

            // Non-vacuity: if the body were not found, or found empty, the set below would be empty
            // and would not equal the expectation — but say so out loud anyway.
            Assert.Contains("return", body, StringComparison.Ordinal);

            var determinants = Regex.Matches(body, @"_[A-Za-z][A-Za-z0-9]*")
                .Select(m => m.Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            // ── ACKNOWLEDGED ADDITION, 2026-09-07 (no-server-idle, dev main 118feec) ──
            // _hasFallback joined this set and the lint fired on the merge-push CI run, which is
            // exactly what it is for: it made a silent addition loud. Acknowledged rather than
            // widened blindly, with the justification recorded here so the next reader does not
            // have to reconstruct it.
            //
            // WHY IT BELONGS: Adrian's fresh-eyes ruling 4 removed the implicit "Server=." local
            // default. GetCurrentConnectionString must therefore distinguish "an explicit fallback
            // was configured" from "nothing is configured at all" — the first returns the decrypted
            // fallback, the second leaves the factory Unconfigured so callers fail BEFORE any
            // network attempt instead of silently probing localhost. _hasFallback IS that
            // distinction (set at :60/:65, read at :154), so it is a determinant of the returned
            // connection string in the same sense as the three below, not an incidental field.
            //
            // It widens no surface: it is a private bool on the factory itself, reachable from no
            // caller, and it removes a connection target rather than adding one. Whoever next sees
            // this test fail must justify their own addition the same way rather than editing the
            // expectation to make the build green.
            Assert.Equal(
                new[] { "_encryptedFallbackConnStr", "_hasFallback", "_instanceSelector", "_serverConnectionManager" },
                determinants);
        }

        // ── helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// The Data Source the factory would actually connect to, read off an unopened connection.
        /// </summary>
        private static string DataSourceOf(SqlServerConnectionFactory factory)
        {
            using var connection = factory.CreateConnection();
            return new SqlConnectionStringBuilder(connection.ConnectionString).DataSource;
        }

        /// <summary>
        /// One method's body by brace matching. A "to the next member" window was not used here
        /// because this file does not own <c>SqlServerConnectionFactory</c>'s layout, and a window
        /// that can silently include the NEXT method would let a determinant introduced there
        /// satisfy the pin above.
        /// </summary>
        private static string MethodBody(string text, string signature)
        {
            var at = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at > 0, signature + " no longer exists — re-point this pin at whatever replaced it.");

            var open = text.IndexOf('{', at + signature.Length);
            Assert.True(open > 0, signature + " has no body — re-point this pin.");

            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text[open..(i + 1)];
            }

            Assert.Fail(signature + " has an unbalanced body — re-point this pin.");
            return string.Empty;
        }

        /// <summary>
        /// An authorised caller, built the same way the viewer is so the two differ in exactly one
        /// thing: the role.
        /// </summary>
        private AppUserState AdminUserState()
        {
            var state = new AppUserState(
                HostEnvironmentInfo.BrowserHosted,
                EnforcedRbac(),
                new ServiceCollection().BuildServiceProvider(),
                NullLogger<AppUserState>.Instance);
            state.SetRole(AppRoles.Admin);
            Assert.True(state.IsAuthorized("run_scripts"));
            return state;
        }

        /// <summary>
        /// Points the manager at a connection the way an authorised operator would, so a test's
        /// ARRANGE step does not have to pretend to be the thing under test.
        /// </summary>
        private sealed class ServerConnectionManagerAdmin
        {
            private readonly ServerConnectionManager _manager;
            internal ServerConnectionManagerAdmin(ServerConnectionManager manager) => _manager = manager;

            internal void PointAt(string connectionId)
            {
                // Establish from "none" is exactly what an arrange step is doing, and it goes
                // through the same method everything else does — no back door into _currentServerId
                // exists, which is itself part of what makes this a chokepoint.
                var outcome = _manager.SetCurrentServer(connectionId, ConnectionRetargetGrant.Establish);
                if (!outcome.Applied)
                    throw new InvalidOperationException(
                        "Arrange failed: " + outcome.RefusedBecause);
            }
        }

        private static IEnumerable<string> SourceFiles(string root)
        {
            // Mcp\ added 2026-08-07: the fix round's note that "no other top-level directory holds
            // app code today" was false — Mcp\ holds three shipped .cs files (McpReadOnlyService,
            // McpServiceRegistration, SqlTriageMcpTools) that this scan had never looked at. The
            // compile-time grant means the gap was lint-only, but a scan that silently skips app
            // code is the instrument-blindness this whole wave is about.
            foreach (var dir in new[] { "Components", "Pages", "Data", "Cli", "Services", "Mcp" })
            {
                var full = Path.Combine(root, dir);
                if (!Directory.Exists(full)) continue;

                foreach (var file in Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories))
                {
                    if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                      StringComparison.Ordinal)) continue;
                    if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                      StringComparison.Ordinal)) continue;
                    if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                        yield return file;
                }
            }
        }

        private static string Rel(string root, string file) =>
            file.Length > root.Length ? file[(root.Length + 1)..] : file;
    }
}
