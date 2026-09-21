/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT: <b>(S-1) every read this app issues against a client's production server carries
/// the session-safety preamble</b> — READ UNCOMMITTED so the read takes no shared locks, and a
/// LOCK_TIMEOUT so it aborts instead of hanging behind someone else's DDL.
/// <see cref="SqlSessionSafety"/> is the one definition of it.
///
/// <para><b>WHY A CENSUS AND NOT ANOTHER FIXED TEST.</b> S-1 was written down as PROSE at
/// <c>SqlSessionSafety.cs:11</c> — "every read this app issues against a client's production server" —
/// and three services applied it while a fourth did not. <c>AlertEvaluationService</c>, the alert
/// loop, ran SIX recurring background reads against monitored production servers on a timer with no
/// preamble at all, and nothing anywhere went red. On this project that is not a theoretical cost:
/// PerfMon has already caused TWO client outages, so "our monitoring degraded their server" is a
/// realised risk. A sentence in a doc comment is not a guard; this is.</para>
///
/// <para><b>WHAT MAKES THIS STRUCTURAL.</b> <see cref="SessionSafetyAnalyzer"/> reads IL and resolves
/// metadata tokens — it never matches source characters. That matters because this repo has MEASURED
/// character censuses failing in both directions on 2026-09-11: a line-wrapped offender stayed
/// invisible to five of them, and prose inside a string literal counted as a real instance. A
/// reformat, a rename or an extracted local cannot move this test.</para>
///
/// <para><b>WIDENED 2026-09-15.</b> Until this lane the census keyed on a method calling
/// <c>GetConnectionString</c> IN ITS OWN BODY, which reached 17% of the surface — 49 of 291
/// command-building methods. The analyser now follows the connection ACROSS method boundaries, so the
/// shape that hid <c>AccessSurfaceCollector</c>'s six commands and the whole of <c>XEventService</c>
/// is in scope. The boundary that remains is named on <see cref="SessionSafetyAnalyzer"/> itself.</para>
/// </summary>
public sealed class SessionSafetyCensusTests
{
    private readonly ITestOutputHelper _out;
    public SessionSafetyCensusTests(ITestOutputHelper output) => _out = output;

    private static Assembly Product => typeof(ConfigFileHelper).Assembly;

    /// <summary>
    /// The floor the alert loop's own site count may not fall below. It is a FLOOR, not a pinned
    /// count: a seventh site is welcome and needs no edit here, but the count collapsing to zero —
    /// the shape a broken IL walk, a renamed type or a changed token produces, and the greenest
    /// possible result for a filter-only census — turns this RED instead.
    ///
    /// <para>It is frozen BY MEASUREMENT against the product assembly, so it only means anything
    /// in the configuration it was measured in;
    /// <see cref="RequireTheBuildConfigurationTheseNumbersWereFrozenIn"/> is asserted before it.</para>
    /// </summary>
    private const int AlertLoopSiteFloor = 6;

    /// <summary>
    /// The floor the WHOLE census may not fall below, MEASURED on 2026-09-15 on both build axes. The
    /// widening took the population from 49 methods to 96 (full) / 73 (community); pinning a floor means
    /// a later change that re-narrows the analyser — a broken taint edge, a renamed source, a fixpoint
    /// that stops early, a wrapper type that launders the connection — goes RED. Without it a narrower
    /// analyser reports LESS debt, and less debt reads as progress. That is not hypothetical: while this
    /// lane was being built, one tightening silently dropped the entire alert loop out of the census and
    /// only <see cref="AlertLoopSiteFloor"/> caught it.
    ///
    /// <para><b>⚠ THE TWO NUMBERS ARE NOT INTERCHANGEABLE.</b> The community profile Compile-Removes
    /// whole subsystems — <c>AccessSurfaceCollector</c>, Portal collectors, Mcp — so its census is
    /// legitimately smaller. Asserting the full-profile floor against a community build would fail for a
    /// reason that has nothing to do with S-1, and "fixing" that by lowering the floor to 73 would blind
    /// the full build, where 23 more methods live. The profile is detected from the assembly itself, not
    /// from a compile-time symbol, because this test must give the right answer about whatever assembly
    /// it was handed.</para>
    ///
    /// <para><b>⚠ FULL FLOOR RAISED 96 -> 103 on 2026-09-16 (lane s1-census-blind-to-the-factory, session Customer update path lane [8a308e]),
    /// MEASURED IN RELEASE at <c>69ca500</c></b> with the analyser's
    /// <c>DbConnectionStringBuilder</c> edge applied (<c>SessionSafetyAnalyzer</c>,
    /// <c>IsConnectionCarrying</c>). Release reports 103 methods / 129 sites / 92 unguarded,
    /// against 96 / 120 / 86 before it. <b>The population did not grow — the analyser stopped
    /// losing it THROUGH ONE OF ITS TWO LAUNDERING MECHANISMS.</b></para>
    ///
    /// <para><b>⚠⚠ MECHANISM #2 IS CLOSED — 2026-09-16, lane census-blind-to-interface-dispatch.
    /// AND THE ATTRIBUTION IT WAS CLOSED AGAINST WAS PARTLY WRONG.</b> The paragraph this replaces
    /// said this analyser resolves callees ONLY by <c>(Module, MetadataToken)</c>
    /// (<c>SessionSafetyAnalyzer.cs:533</c>, <c>:339</c>) — TRUE — and then put all three known blind
    /// instances down to INTERFACE DISPATCH — NOT true. There were TWO mechanisms and FOUR instances.
    /// <list type="bullet">
    /// <item><b>Interface dispatch.</b> An interface member has no IL, so it was not in the graph at
    /// all and its implementation's <c>ReturnsTainted</c> was never consulted.
    /// <c>Data/SessionDataService.cs</c> was the proof inside ONE file: the field is
    /// <c>IDbConnectionFactory</c> (<c>:25</c>), <c>FetchSessionsAsync</c> casts to the concrete type
    /// at <c>:144</c> and was already counted below, while <c>GetBlockingChainAsync</c> (<c>:195</c>),
    /// <c>GetQueryPlanAsync</c> (<c>:300</c>) and <c>KillSessionAsync</c> (<c>:348</c>) take the
    /// interface and were ABSENT on both profiles — <b>one of four visible, and the invisible one
    /// runs <c>KILL {spid}</c></b> at <c>:353</c>. <c>Data/Services/BenchmarkService.cs</c> is the
    /// second instance (connection <c>:54</c>, five commands at <c>:103</c>, <c>:128</c>,
    /// <c>:158</c>, <c>:214</c>, <c>:226</c>), and there is a THIRD the brief that opened this lane
    /// did not name: <c>Data/Services/RiskReport/LiveRiskAssessmentSource.cs</c> (field <c>:21</c>,
    /// connection <c>:38</c>, commands <c>:60</c> and <c>:91</c>). A brief that names three when
    /// there are four invites a builder to fix three.</item>
    /// <item><b>The delegate boundary</b> — <c>ldftn</c> was not a call edge at all, and
    /// <c>Delegate.Invoke</c> has no body, so a connection opened inside a ZERO-ARGUMENT lambda never
    /// reached the method that invoked the delegate. <c>Data/Services/AgentMailChainProbe.cs</c> is
    /// this mechanism and ONLY this one: its field at <c>:318</c> is the CONCRETE
    /// <c>SqlServerConnectionFactory</c>, not the interface, so the interface story never applied to
    /// it. All six of its connections are built inside <c>Func&lt;DbConnection&gt;</c> lambdas
    /// (<c>:494</c>, <c>:503</c>, <c>:557</c>, <c>:561</c>, <c>:925</c>, <c>:933</c>) and consumed in
    /// the <c>...WithAsync</c> methods.</item>
    /// </list>
    /// ⚠ And the <c>ConnectionFactoryOverride</c> hook at <c>AgentMailChainProbe.cs:1011-1012</c> is
    /// NOT a third mechanism. That is PROVED rather than argued:
    /// <c>Data/Services/ServerConfigScriptService.cs:94</c> carries the IDENTICAL
    /// <c>Func&lt;string, DbConnection&gt;</c> hook, and its <c>RunAsync</c> and
    /// <c>RunScriptCoreAsync</c> were ALREADY frozen rows below — which is only possible if that shape
    /// propagates. The difference is ARITY: with a tainted STRING argument the pre-existing rule
    /// fires; with zero arguments nothing did.
    ///
    /// ★ <b>READ THIS CLASS THE SAME WAY REGARDLESS.</b> <c>Data/SqlSessionSafety.cs:62</c> already
    /// says it: a green census means <i>no KNOWN-SHAPED unguarded site</i>, never <i>S-1 holds</i>.
    /// Closing a mechanism retires one shape. The boundaries that remain are listed on
    /// <see cref="SessionSafetyAnalyzer"/> itself, and this lane ADDED two rather than removing them:
    /// ARGUMENT taint and GUARDEDNESS still do not travel a dispatch edge (so a connection obtained
    /// through an interface and already covered upstream would read as FALSE debt), and a dispatch
    /// edge is recorded only when the DECLARATION is declared in the assembly under
    /// analysis.</para>
    ///
    /// <para><b>AND THE REASON FOR THE 96 -> 103 RAISE ABOVE</b>, which the paragraph order had
    /// obscured by splicing the mechanism-#2 block into the middle of this sentence:
    /// <c>SqlServerConnectionFactory.CreateConnection(string)</c> round-trips a
    /// monitored connection string through <c>SqlConnectionStringBuilder</c>
    /// (<c>Data/SqlServerConnectionFactory.cs:85</c>), and until the taint chain crossed that
    /// type every command built on a connection from that overload was invisible here. ⚠ RAISING
    /// THIS IS THE POINT: the assertion is <c>BeGreaterThanOrEqualTo</c>, so leaving it at 96
    /// would have gone on passing while all seven newly visible methods vanished again.</para>
    ///
    /// <para><b>The seven rows added below share ONE cause and ONE target proof, so it is written
    /// here once instead of seven times.</b> Every one of them reaches its connection through that
    /// same <c>CreateConnection(string)</c> overload — one laundering site, seven consumers. And
    /// every one is aimed at a MONITORED server, not at the app's own store: that overload
    /// resolves, in this order, <c>GlobalInstanceSelector</c> (<c>:118-131</c>), then
    /// <c>CurrentServer</c> (<c>:133-143</c>), then the configured fallback (<c>:154</c>), then the
    /// enabled connections (<c>:157</c>), and THROWS when nothing is configured (<c>:166</c>) —
    /// all in <c>Data/SqlServerConnectionFactory.cs</c>, and there is no local-store branch in any
    /// of them. ⚠ Corrected by the cold gate: an earlier draft of this comment omitted the
    /// <c>CurrentServer</c> branch entirely and cited <c>:165-185</c>, a range that spans the throw
    /// and <c>IsUnconfigured</c>, two different members. The conclusion is unchanged — every
    /// branch resolves a monitored server — but a resolution order stated wrongly is the kind of
    /// comment the next reader will act on.
    /// The local store's own <c>IDbConnectionFactory</c>,
    /// <c>liveQueriesConnectionFactory</c> (<c>Data/SqliteConnectionFactory.cs:14</c>), is
    /// registered NOWHERE and referenced only at its own declaration; both hosts bind
    /// <c>IDbConnectionFactory</c> to <c>SqlServerConnectionFactory</c>
    /// (<c>Data/ServiceCollectionExtensions.cs:50</c>,
    /// <c>Data/Services/WindowsServiceHost.cs:478</c>). So <c>LocalStoreSite</c> stays absent from
    /// the census and none of these seven is a false entry on the target question.</para>
    ///
    /// <para><b>✅ RAISED BY THE 2026-09-16 DISPATCH LANE FROM A MEASURED RELEASE RUN ON EACH
    /// AXIS.</b> That lane widened the analyser to follow interface dispatch and <c>ldftn</c> (see
    /// <see cref="SessionSafetyAnalyzer"/>). Measured, not predicted: full <b>103 -> 117 methods,
    /// 129 -> 144 sites, 92 -> 106 unguarded</b>; community <b>79 -> 91 methods, 105 -> 118 sites,
    /// 69 -> 81 unguarded</b>. Both runs printed <c>optimized=True</c> off
    /// <c>DebuggableAttribute</c>, so both are RELEASE, at <c>97f158f</c>, product
    /// <c>SQLTriage 0.99.0.4070</c>.
    /// <b>FOURTEEN newly visible methods on the full axis, TWELVE on community</b> — the difference is
    /// <c>LiveRiskAssessmentSource</c>'s two, Compile-Removed from community by
    /// <c>buildprofile.targets:220</c>.
    /// ⚠⚠ <b>An earlier draft of this paragraph said the opposite</b> — that the number was
    /// UNMEASURED, that the floors were deliberately left at their pre-lane values, and that raising
    /// them was still owed. That was true of the BUILD stage, which ran no build and said so
    /// honestly; it stopped being true when the orchestrator measured both axes. The cold gate found
    /// the stale text sitting three lines above the raised constants and ruled it HIGH, because a
    /// reader seeing <c>117</c> beside the words "the new number is unmeasured" would reasonably
    /// conclude a prediction had been written in as a measurement — which is the one thing this
    /// class exists to prevent. ★ <b>Prose that contradicts the constant beside it is worse than no
    /// prose: the constant is right and the reader cannot tell.</b></para>
    ///
    /// <para><b>THE FOURTEEN ROWS THE DISPATCH LANE ADDED share TWO causes, written here once.</b> Ten
    /// reach their connection through <c>IDbConnectionFactory</c> and two through a
    /// <c>Func&lt;DbConnection&gt;</c> lambda, and two more — <c>ChangedObjectsService</c>'s pair — became
    /// reachable through the same widening and were NOT predicted by the build stage; the census going
    /// RED is what found them. The per-row comments below name which cause, plus every
    /// site's <c>file:line</c>, a READ/WRITE/CANNOT-TELL verdict and a MONITORED/LOCAL/CANNOT-TELL
    /// target. All fourteen are MONITORED for the same reason the seven above are: the only
    /// implementation of that interface reachable at runtime is
    /// <c>SqlServerConnectionFactory</c> (both hosts bind it —
    /// <c>Data/ServiceCollectionExtensions.cs:50</c>, <c>Data/Services/WindowsServiceHost.cs:478</c>),
    /// its <c>GetCurrentConnectionString</c> resolves a monitored server in every branch and THROWS
    /// when nothing is configured (<c>Data/SqlServerConnectionFactory.cs:118-169</c>), and the
    /// local-store sibling <c>liveQueriesConnectionFactory</c> is registered nowhere — re-derived at
    /// <c>97f158f</c>, its only reference outside its own declaration is a comment in THIS file. And
    /// all fourteen are <c>prefixes=0</c> because all FIVE owning types contain ZERO references to
    /// <see cref="SqlSessionSafety"/>: counted, not assumed.</para>
    /// </summary>
    // ✅ RATCHETED 103 -> 117, 2026-09-16 (lane census-blind-to-interface-dispatch, session
    // Customer update path lane [8a308e]). MEASURED in a RELEASE build on the FULL axis at
    // 97f158f with interface dispatch and ldftn followed: "117 methods, 144 sites, 106 unguarded,
    // 49 visible to the pre-widening census", from this class's own StdOut
    // (evidence/census-blind-to-interface-dispatch-2026-09-17/iface-full.trx).
    // This is the FULL figure. Community is 91 and is annotated separately below.
    private const int FullProfileCensusFloor = 117;
    // ✅ RATCHETED 79 -> 91, 2026-09-16 (lane census-blind-to-interface-dispatch, session Customer
    // update path lane [8a308e]). MEASURED in a RELEASE COMMUNITY build at 97f158f with interface
    // dispatch and ldftn followed: "91 methods, 118 sites, 81 unguarded, 41 visible to the
    // pre-widening census", from this class's own StdOut
    // (evidence/census-blind-to-interface-dispatch-2026-09-17/iface-community.trx).
    // NOT copied from the full-profile figure: full is 117, community is 91, and a floor that names
    // the wrong profile's number is the defect this whole arc exists to stop.
    // PRIOR RATCHET, kept because a floor's history is how you audit it: 73 -> 79 on 2026-09-16 by
    // lane s1-census-blind-to-the-factory, measured at 69ca500 when the DbConnectionStringBuilder
    // taint edge closed ("79 methods, 105 sites, 69 unguarded").
    // ⚠ The 79 -> 91 header above replaced a comment still titled "RATCHETED 73 -> 79" that sat over
    // the constant 91 and said "community is 79, full is 103". The cold gate caught it: a stale
    // provenance comment is how a correct constant acquires a false pedigree.
    private const int CommunityProfileCensusFloor = 91;

    /// <summary>
    /// Which profile the product assembly under test was built with, asked of the ASSEMBLY.
    /// <c>AccessSurfaceCollector</c> is Compile-Removed from the community profile, so its presence
    /// distinguishes the two. Deliberately not a <c>#if</c>: the test assembly and the product assembly
    /// are built together today, and a symbol would keep answering about the test project even if that
    /// ever stopped being true.
    /// </summary>
    private static bool IsFullProfile =>
        Product.GetType("SQLTriage.Data.Services.AccessSurfaceCollector", throwOnError: false) != null;

    private static int MeasuredCensusFloor =>
        IsFullProfile ? FullProfileCensusFloor : CommunityProfileCensusFloor;

    /// <summary>
    /// Whether the PRODUCT assembly under test was built with the JIT optimizer ON, asked of the
    /// ASSEMBLY for the same reason <see cref="IsFullProfile"/> is: this test must give the right
    /// answer about whatever assembly it was handed, not about the configuration the test project
    /// happened to be compiled in.
    ///
    /// <para><b>⚠ A MISSING ATTRIBUTE MEANS OPTIMIZED, which is the opposite of how it reads.</b>
    /// <see cref="DebuggableAttribute"/> is emitted in order to turn JIT optimization OFF; an assembly
    /// carrying none is optimized by default. So <c>null</c> must answer Release here and never
    /// "unknown" — reading absence as a failure would fire this guard on assemblies it has no
    /// complaint about.</para>
    ///
    /// <para><b>MEASURED 2026-09-16 at <c>b295c12</c>, not assumed.</b> Both built copies of this
    /// product assembly were loaded and read through this same API. <c>-c Debug</c>: attribute PRESENT,
    /// <c>IsJITOptimizerDisabled=True</c>, flags <c>Default, IgnoreSymbolStoreSequencePoints,</c>
    /// <c>EnableEditAndContinue, DisableOptimizations</c>. <c>-c Release</c>: attribute PRESENT,
    /// <c>IsJITOptimizerDisabled=False</c>, flags <c>IgnoreSymbolStoreSequencePoints</c>. It
    /// discriminates, and it is PRESENT on BOTH axes — the null branch is defensive, not the
    /// observed case for this product.</para>
    /// </summary>
    private static bool ProductWasBuiltOptimized
    {
        get
        {
            var debuggable = Product.GetCustomAttribute<DebuggableAttribute>();
            return debuggable is null || !debuggable.IsJITOptimizerDisabled;
        }
    }

    /// <summary>
    /// THE ONE GUARD, called by every test in this class whose verdict rests on a number FROZEN BY
    /// MEASUREMENT against the product assembly — <see cref="FullProfileCensusFloor"/>,
    /// <see cref="CommunityProfileCensusFloor"/>, <see cref="AlertLoopSiteFloor"/>, and the
    /// <c>sites=</c>/<c>prefixes=</c> counts inside <see cref="FrozenUnguarded"/>.
    ///
    /// <para><b>THE INVARIANT: this census may not report on a build configuration its numbers were
    /// not frozen in.</b> It is ONE method rather than a copy per test on purpose — four hand-edited
    /// copies is how a SET gets shipped as an INSTANCE. A test added to this class later either calls
    /// this or says at its own body why it does not; it must not grow a fifth copy.</para>
    ///
    /// <para><b>IT FAILS; IT NEVER SKIPS.</b> No <c>Skip</c>, no conditional <c>[Fact]</c>, no early
    /// <c>return</c> — a skipped test renders identically to a passing one, and a green this class
    /// did not earn is the exact thing it exists to prevent.</para>
    ///
    /// <para>Deliberately NOT called by
    /// <see cref="The_debt_list_goes_red_when_a_frozen_entry_is_fixed_by_either_form"/> or by
    /// <see cref="The_analyser_flags_reached_sites_and_clears_guarded_and_local_ones"/>. The reason is
    /// written at each of them, because that is where the next reader tempted to complete the set
    /// will be standing.</para>
    /// </summary>
    private void RequireTheBuildConfigurationTheseNumbersWereFrozenIn()
    {
        var debuggable = Product.GetCustomAttribute<DebuggableAttribute>();
        _out.WriteLine($"product assembly {Product.GetName().Name} {Product.GetName().Version}: "
                     + (debuggable is null
                            ? "DebuggableAttribute ABSENT (absence means the JIT optimizes)"
                            : $"DebuggableAttribute IsJITOptimizerDisabled={debuggable.IsJITOptimizerDisabled}"
                              + $" flags={debuggable.DebuggingFlags}")
                     + $" -> optimized={ProductWasBuiltOptimized}");

        // ⚠ THE FAILURE TEXT IS A SAFETY SURFACE. It names what to CHECK, it names the wrong repair
        // someone will otherwise reach for, and it stays correct when the guard itself is the false
        // positive. It prescribes exactly one action — re-run in Release — which is safe in every case.
        ProductWasBuiltOptimized.Should().BeTrue(
            "THIS CLASS WAS RUN IN A CONFIGURATION ITS NUMBERS WERE NOT FROZEN IN, so nothing it "
            + "reports is evidence about S-1. The product assembly carries DebuggableAttribute with the "
            + "JIT optimizer DISABLED — a Debug build — while every frozen number in this class ("
            + nameof(FullProfileCensusFloor) + ", " + nameof(CommunityProfileCensusFloor) + ", "
            + nameof(AlertLoopSiteFloor) + ", and the sites=/prefixes= counts in "
            + nameof(FrozenUnguarded) + ") was measured against a RELEASE build. MEASURED 2026-09-16 "
            + "at b295c12 on the full axis: the IDENTICAL commit reports 120 command sites in Release "
            + "and 117 in Debug. Debug UNDERCOUNTS DatabaseAvailabilityService.DatabaseExistsAsync, "
            + "MaintenanceScriptService.GenerateIndexMaintenanceScriptAsync and "
            + "MaintenanceScriptService.GenerateStatisticsUpdateScriptAsync by one site each. WHAT TO "
            + "CHECK: re-run this class with -c Release. That is what CI and "
            + "tools/record-full-profile-build.ps1 run, and it is the configuration these numbers are "
            + "frozen against; a bare 'dotnet test' defaults to Debug and lands here. ⚠ DO NOT EDIT "
            + "THE FROZEN NUMBERS TO MATCH WHAT A DEBUG RUN REPORTS. That repair is attractive — the "
            + "count failure this guard now pre-empts printed the lower Debug counts in its own text "
            + "and told you to replace the rows with them — and it reverts this class to the "
            + "undercount it was refrozen away from on 2026-09-16, turning CI and main RED while "
            + "reading green on your machine. ⚠ AND IF THIS GUARD IS ITSELF THE FALSE POSITIVE — you "
            + "believe you ARE in Release and it fired anyway — then what to check is the ASSEMBLY, "
            + "not the numbers. This reads the attribute off whichever SQLTriage.dll the test host "
            + "actually loaded, so a stale DLL left in bin/ by an earlier build, or a csproj that sets "
            + "Optimize independently of the configuration name, answers for that DLL and not for your "
            + "command line. The line printed above names the assembly, its version and the exact "
            + "flags it carries. Establish which assembly was loaded before changing anything here.");
    }

    private static bool IsAlertLoop(Type t) => t.FullName == "SQLTriage.Data.Services.AlertEvaluationService";

    /// <summary>
    /// THE DEBT THIS LANE DID NOT PAY, frozen so it cannot grow.
    ///
    /// <para><b>⚠ EACH ENTRY CARRIES ITS SITE AND PREFIX COUNTS, and that is load-bearing.</b> Until
    /// 2026-09-15 this list froze by METHOD NAME alone, and the cold gate PROVED what that permits: it
    /// added a second unguarded command build inside an already-frozen method
    /// (<c>WaitStatsService.GetSnapshotAsync</c>), the census went <c>sites=1</c> → <c>sites=2</c>, and
    /// all three tests stayed GREEN. Every frozen method could accumulate unlimited new unguarded
    /// production reads in silence. With the counts in the key, that same edit changes the key, the key
    /// is not in this set, and the ratchet fires.</para>
    ///
    /// <para><b>WHY THESE ARE NOT FIXED HERE.</b> Each needs the same per-site judgement the alert loop
    /// got: whether the command is a READ (where READ UNCOMMITTED is the safety control) or a WRITE.
    /// Several plainly are not reads — <c>AgentJobControlService</c> starts and stops Agent jobs,
    /// <c>ScheduledTaskEngine</c> and <c>QueryPlanModal</c> are not read lanes — and wrapping a write in
    /// READ UNCOMMITTED would be a DEFECT, not a fix. That judgement was made for the 43 entries the
    /// narrow census could see: the classification is at
    /// <c>evidence/s1-preamble-census-2026-09-15/census-43.csv</c> in the project's private evidence archive (33 READ /
    /// 6 WRITE / 3 MIXED / 1 UNDETERMINED). The entries this widening ADDED have had no such pass and
    /// must not be swept blind.</para>
    ///
    /// <para><b>⚠ RE-MEASURED 2026-09-16, AND THE COUNTS WERE WRONG FROM BIRTH.</b> Four rows here
    /// disagreed with what the analyser in the SAME COMMIT reported, and main was RED on both axes
    /// from <c>f137afc</c> onward because of it. The obvious story — "the analyser was widened again
    /// at <c>74d6f69</c> and nobody re-measured" — is WRONG, and was refuted by running this census
    /// against <c>f137afc</c>'s own analyser and against <c>f137afc</c>'s whole tree: the identical
    /// four rows come back either way.</para>
    ///
    /// <para><b>⚠⚠ CORRECTED 2026-09-16 BEFORE LANDING — THE STATED CAUSE ABOVE WAS WRONG, AND THE REAL
    /// ONE IS SHARPER.</b> This comment originally said the list "was simply never produced by a run of
    /// the analyser it shipped beside". It WAS produced — in <b>Debug</b>, where it is GREEN at
    /// <c>a400e91</c> to this day. <b>THIS CENSUS RETURNS DIFFERENT COUNTS IN DEBUG AND IN RELEASE AT THE
    /// SAME COMMIT.</b> Release reports one MORE site in exactly three methods; Debug UNDERCOUNTS them.
    /// Measured on the full axis at <c>a400e91</c>, both runs .trx-verified at EXECUTED=4:
    /// <c>-c Debug</c> → 4 passed, 0 failed (GREEN); <c>-c Release</c> → 3 passed, 1 failed (RED), naming
    /// <c>DatabaseExistsAsync sites=2</c>, <c>GenerateIndexMaintenanceScriptAsync sites=3</c>,
    /// <c>GenerateStatisticsUpdateScriptAsync sites=2</c> — which are exactly the counts frozen below.</para>
    ///
    /// <para><b>⚠⚠ SO: A BARE <c>dotnet test</c> GOES RED ON THIS CLASS. THAT IS EXPECTED, NOT A
    /// REGRESSION.</b> <c>dotnet test</c> defaults to Debug; CI and <c>tools/record-full-profile-build.ps1</c>
    /// run <b>Release</b>, and Release is the configuration these counts are frozen against. No frozen list
    /// can be green in both. <b>If you are about to "fix" a red here by lowering these counts back to
    /// 1/2/1, STOP — that reverts to the Debug numbers and re-breaks CI.</b> Re-run with <c>-c Release</c>
    /// first. Whatever you freeze, record WHICH CONFIGURATION you measured in.</para>
    ///
    /// <para><b>ENFORCED since 2026-09-16, because the paragraph above was the only thing standing
    /// between a Debug run and a wrong edit to these rows.</b>
    /// <see cref="RequireTheBuildConfigurationTheseNumbersWereFrozenIn"/> now refuses the
    /// measurement outright, so a Debug run never reaches the count comparison whose own failure
    /// text named the lower Debug numbers and invited you to freeze them.</para>
    ///
    /// <para><b>The lesson stands even though the cause changed: "a frozen baseline that has never been
    /// observed GREEN is not a baseline"</b> — a freeze commit must be red-then-green in its own right,
    /// in a NAMED configuration, or it pins a number nobody measured.</para>
    ///
    /// <para><b>THIS LIST MAY ONLY SHRINK.</b> It is asserted in both directions below: nothing new
    /// may join it, and an entry that gets fixed must be DELETED from it or the test goes red. It is
    /// a record of debt, never an approval.</para>
    /// </summary>
    private static readonly string[] FrozenUnguarded =
    {
        "SQLTriage.Cli.EphemeralConnectionFactory.PreflightWithIdentityAsync sites=1 prefixes=0",
        "SQLTriage.Components.Shared.ConnectionDialog.TestConnection sites=1 prefixes=0",
        "SQLTriage.Components.Shared.DynamicDashboard.ExecuteActionSqlAsync sites=2 prefixes=0",
        // ⚠ ADDED 2026-09-16 (lane s1-census-blind-to-the-factory, session Customer update path lane [8a308e]). NEWLY VISIBLE, not new code —
        // cause and target proof at FullProfileCensusFloor. Components/Shared/
        // DynamicDashboard.razor:1323 (cmd) on the connection opened at :1319,
        // CreateConnection("master"); the SQL is the literal at :1318, "SELECT name FROM
        // sys.databases WHERE is_query_store_on = 1 ORDER BY database_id ASC". READ + MONITORED.
        // The whole-rental question is answered ONE COMMAND: the connection is opened, used once
        // and disposed at method exit, so no write shares this session. The only mutation the
        // method makes is to ConnectionManager.CurrentServer.Database at :1338 — an in-process
        // object, not SQL. Fix OWED, sweep's.
        "SQLTriage.Components.Shared.DynamicDashboard.LoadQueryStoreDatabases sites=1 prefixes=0",
        "SQLTriage.Components.Shared.DynamicDashboard.RunAgentJobAsync sites=1 prefixes=0",
        "SQLTriage.Components.Shared.DynamicDashboard.ShowJobDetails sites=1 prefixes=0",
        "SQLTriage.Components.Shared.PanelEditorModal.DetectColumns sites=3 prefixes=0",
        "SQLTriage.Components.Shared.PanelEditorModal.TestQuery sites=1 prefixes=0",
        "SQLTriage.Components.Shared.QueryPlanModal.ExecuteSingleIndex sites=1 prefixes=0",
        "SQLTriage.Data.CheckExecutionService.DetectEngineEditionAsync sites=1 prefixes=0",
        "SQLTriage.Data.CheckExecutionService.ExecuteSingleCheckAsync sites=1 prefixes=0",
        // ⚠ COUNT CORRECTED 1 -> 2, 2026-09-16 (a peer session). The row was never right; the
        // method did not grow. BOTH sites are READS of sys.databases —
        // Data/DatabaseAvailabilityService.cs:59 builds on a ServerConnectionManager ENABLED
        // connection, :78 on SqlServerConnectionFactory, which resolves CurrentServer or an enabled
        // connection or the configured fallback and THROWS when unconfigured
        // (Data/SqlServerConnectionFactory.cs:165-185) — so neither site can land on the app's own
        // local store. READ + MONITORED on both: the S-1 fix is OWED and belongs to the sweep.
        "SQLTriage.Data.DatabaseAvailabilityService.DatabaseExistsAsync sites=2 prefixes=0",
        "SQLTriage.Data.HealthCheckService.GetHealthStatusAsync sites=6 prefixes=0",
        "SQLTriage.Data.HealthCheckService.PopulateLastSeenAsync sites=2 prefixes=0",
        // ⚠ ADDED 2026-09-16 (lane s1-census-blind-to-the-factory, session Customer update path lane [8a308e]). NEWLY VISIBLE, not new code.
        // sites=2 is TWO OVERLOADS carrying one command each, NOT a method that grew:
        // Data/QueryExecutor.cs:88 (the DataTable overload declared at :47) and :199 (the generic
        // mapper overload at :159). Connections at :72 and :184, both
        // sqlFactory.CreateConnection(defaultDatabase). MONITORED.
        // ⚠ THE COMMAND TEXT IS NOT A LITERAL — it is _configService.GetQuery(queryId, ...), a
        // dashboard panel query out of Config/dashboard-config.json, so the SITE carries no
        // read-vs-write guarantee of its own. READ for the SHIPPED configuration, checked and not
        // assumed: all 223 "sqlServer" panel queries were parsed, 12 match a write verb, and all
        // 12 are reads in effect, in THREE categories — the first two were the only ones this
        // comment originally named, and the cold gate found they cover just 4 of the 12:
        //   (a) #temp / table-variable scratch that is then SELECTed (dbh.high_vlf_count,
        //       security.public_role_permissions);
        //   (b) the verb sits inside a STRING LITERAL (dbh.file_growth_7d's ELSE branch RETURNS the
        //       advisory text "...enable: sp_configure ''default trace enabled'', 1; RECONFIGURE"
        //       and executes nothing);
        //   (c) ⚠ EIGHT are DYNAMIC SQL — EXEC sp_executesql @sql / EXEC(@sql) / INSERT @t
        //       EXEC(@sql), in qs.state_alert, qs.forced_plans, qs.db_status, qs.forced_plans_list,
        //       qs.regressed_queries, dbh.iqp_configs, security.orphaned_users and
        //       security.failed_logins_1h. Every one's @sql concatenation builds SELECT ... UNION
        //       ALL only. Category (c) was added 2026-09-16 by the cold gate, which re-derived
        //       12-of-223 independently and then found the stated reason did not account for 8 of
        //       them — the verdict was right and the justification was incomplete.
        // Also checked: GetQuery (Data/DashboardConfigService.cs:712-723) can return SqlServerLegacy
        // instead, but sqlServerLegacy is non-empty 0 times in 223, so the sqlServer population is
        // complete for what that method can return. None mutates a monitored database. ⚠ That is a statement about today's shipped panels, NOT an
        // invariant of the site: the config file is user-editable. READ + MONITORED: fix OWED.
        "SQLTriage.Data.QueryExecutor.ExecuteQueryAsync sites=2 prefixes=0",
        // ⚠ ADDED 2026-09-16 (lane s1-census-blind-to-the-factory, session Customer update path lane [8a308e]). NEWLY VISIBLE, not new code.
        // Data/QueryExecutor.cs:259 (cmd) on the connection at :255,
        // CreateConnection(defaultDatabase). Same config-supplied CommandText and the same
        // 223-panel check as ExecuteQueryAsync above, which is not repeated here.
        // READ (shipped config) + MONITORED: fix OWED, sweep's.
        "SQLTriage.Data.QueryExecutor.ExecuteScalarAsync sites=1 prefixes=0",
        // ⚠ COUNT CORRECTED 1 -> 2, 2026-09-16 (lane s1-census-blind-to-the-factory, session Customer update path lane [8a308e]). The method
        // did not grow: the row named one of TWO OVERLOADS and the other became visible with the
        // builder edge. THE SECOND SITE IS Data/QueryExecutor.cs:301 — the parameterless overload
        // declared at :288, connection at :298, sqlFactory.CreateConnection("SQLWATCH"). The
        // already-counted site is :361 (the ServerConnection overload at :340, connection at :359,
        // new SqlConnection(connection.GetConnectionString(target!, "SQLWATCH"))). Both run the
        // same literal (:290 and :345), "SELECT DISTINCT sql_instance FROM
        // dbo.sqlwatch_logger_snapshot_header ORDER BY sql_instance" — READS; the SQLWATCH
        // repository sits on a monitored server, so MONITORED. Fix OWED, sweep's.
        // WHY :361 WAS ALREADY VISIBLE AND :301 WAS NOT (BELIEVE — read, not run): :359 calls
        // ServerConnection.GetConnectionString, a NAMED taint source in
        // SessionSafetyAnalyzer.MonitoredServerConnStringBuilders (:113-114), whereas :298's only
        // route was SqlServerConnectionFactory.CreateConnection(string) — named nowhere in the
        // analyser, and the builder round-trip inside it is where the chain used to break.
        "SQLTriage.Data.QueryExecutor.GetSqlWatchInstanceNamesAsync sites=2 prefixes=0",
        "SQLTriage.Data.Services.AccessSurfaceCollector.GuardedDbReadAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.AccessSurfaceCollector.GuardedServerReadAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.AccessSurfaceCollector.GuardedTraceDbReadAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.AccessSurfaceCollector.GuardedTraceServerReadAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.AccessSurfaceCollector.ProbePrivilegeAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.AccessSurfaceCollector.ReadWindowsGroupMembersAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.AgentJobControlService.ExecuteJobSpAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.AgentJobControlService.UpdateJobEnabledAsync sites=1 prefixes=0",
        // ✅ MEASURED-VISIBLE 2026-09-16 (lane census-blind-to-interface-dispatch).
        // NEWLY VISIBLE, NOT NEW CODE — the DELEGATE half of the cause at FullProfileCensusFloor.
        // ⚠⚠ THE COUNTS IN THESE TWELVE ROWS ARE PREDICTIONS, NOT MEASUREMENTS: the builder could
        // run no build. The Release run is the authority. Correct any count it disagrees with, and
        // DELETE outright any row it does not produce — an orphaned row is never reported by the
        // stale check and silently forgives whatever count it names.
        //
        // GuardedReadAsync: ONE site, Data/Services/AgentMailChainProbe.cs:1027
        // (cmd.CommandText = sql), on the connection the caller's Func<DbConnection> lambda opened.
        // Every read on the mail chain shares this one site.
        // ⚠ The command text is a PARAMETER, so the site carries no read-vs-write guarantee of its
        // own. READ for every call site that exists today, checked: the thirteen callers in this
        // class pass read-only constants over msdb.dbo.sysoperators, sysnotifications,
        // sys.configurations, msdb.sys.service_queues, msdb.dbo.sysmail_profile,
        // sysmail_principalprofile, sysmail_allitems and sysmail_event_log, plus xp_instance_regread,
        // which READS the registry. MONITORED. Fix OWED, sweep's.
        // ⚠ ITS NAME IS A TRAP: "Guarded" there means guarded against EXCEPTIONS, not against
        // sessions — it has nothing to do with S-1.
        "SQLTriage.Data.Services.AgentMailChainProbe.GuardedReadAsync sites=1 prefixes=0",
        // ✅ MEASURED-VISIBLE 2026-09-16, same lane and same delegate cause as the row above.
        // ⚠⚠ THIS ONE IS A WRITE, SO NO READ UNCOMMITTED IS OWED HERE — on a write it is a defect,
        // not a fix. Site Data/Services/AgentMailChainProbe.cs:954,
        // cmd.CommandText = "msdb.dbo.sp_notify_operator" with CommandType.StoredProcedure: it SENDS
        // MAIL. MONITORED. The whole-rental question makes it worse, not better: the connection comes
        // from the caller's lambda (:925 or :933) and this method is the only user of that rental, so
        // there is no read here to protect and nothing to gain. If a session form is ever wanted on
        // this connection it is ApplyAsync(readUncommitted: false), and choosing a LOCK_TIMEOUT for a
        // mail-send lane is not this lane's call. UNKNOWN — needs a human.
        "SQLTriage.Data.Services.AgentMailChainProbe.SendTestNotificationWithAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.AlertBaselineService.SeedOnePairAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Assessment.SqlCheckExecutor.RunAsync sites=1 prefixes=0",
        // ✅ MEASURED-VISIBLE 2026-09-16 (lane census-blind-to-interface-dispatch).
        // NEWLY VISIBLE, NOT NEW CODE — the INTERFACE half of the cause at FullProfileCensusFloor.
        // Counts MEASURED and CONFIRMED in RELEASE on both
        // axes at 97f158f. ★ The confirmation is structural, not a second opinion: NewDebt keys on
        // the WHOLE row text including sites= and prefixes=, so a wrong count cannot pass — it
        // presents as new debt and goes RED. Both axes were GREEN, so every count here was read
        // off the product assembly and then checked by the assertion.
        // See the AgentMailChainProbe block above for what to do
        // when the Release run disagrees.
        //
        // FIVE rows, ONE connection. Data/Services/BenchmarkService.cs:54 opens it through
        // IDbConnectionFactory.CreateConnection() inside RunBenchmarkAsync (:43) — that method
        // builds no command itself and therefore gets NO row — and hands it down as a plain
        // DbConnection. Each of the five below builds one command on it: sites :103, :128, :158,
        // :214, :226.
        // READ + MONITORED on all five, read and not assumed: :103 and :128 are DECLARE / WHILE /
        // SET arithmetic over local variables ending in a SELECT of the elapsed time; :158 counts
        // rows in sys.dm_exec_query_stats, sys.dm_os_memory_clerks and sys.dm_exec_connections;
        // :214 aggregates sys.dm_os_wait_stats; :226 reads sys.dm_os_schedulers. Nothing mutates
        // server state.
        // ⚠ THE WHOLE-RENTAL QUESTION, ASKED AND ANSWERED: all five share the ONE connection opened
        // at :54, and no write runs on it, so one session-scoped ApplyAsync would cover the rental.
        // ⚠ BUT THIS IS A BENCHMARK, and that is a real complication rather than a detail: a
        // LOCK_TIMEOUT and an isolation change become part of what the numbers MEAN. The preamble's
        // VALUES here are a judgement for the sweep, not a mechanical add. :158 and :226 already
        // carry WITH (NOLOCK), so they are also R3 cases.
        // ★ THIS TYPE IS THE RESTATED WITHIN-FILE CONTROL. Its four LOCAL-store methods must stay
        // ABSENT — EnsureBenchmarkTables (site :281), StoreBenchmarkResultsToDedicatedTablesAsync
        // (:337, :360) and GetLatestRuns (:397), every one of them on
        // _cacheStore.CreateExternalConnection(), the app's own encrypted SQLite store
        // (Data/Caching/SqliteCacheStore.cs:182). Both halves are asserted in
        // No_new_monitored_server_query_site_may_skip_session_safety, because the dispatch fix
        // DISSOLVED the SessionDataService control that made this lane provable.
        "SQLTriage.Data.Services.BenchmarkService.GetCpuSchedulerDelayAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.BenchmarkService.GetSignalWaitPercentageAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.BenchmarkService.RunCpuIntegerBenchmarkAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.BenchmarkService.RunMemoryAccessBenchmarkAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.BenchmarkService.RunStringOpsBenchmarkAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.BlockingForensicsService.CaptureBlockingAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.BlockingForensicsService.GetLivePlanForSpidAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.BlockingForensicsService.GetQueryStoreLockWaitsAsync sites=2 prefixes=0",
        "SQLTriage.Data.Services.BlockingForensicsService.ReadAndRecordDeadlocksAsync sites=1 prefixes=0",
        // ⚠ ADDED 2026-09-16 (lane census-blind-to-interface-dispatch, session Customer update
        // path lane [8a308e]). NEWLY VISIBLE, not new code - and NOT predicted by the build stage,
        // which named twelve methods and missed these two. Found by the census going RED, which is
        // the census doing its job.
        // ⚠⚠ I FIRST MISREAD THESE AS THE FLOOD AND WAS WRONG, twice over. (1) I anchored on the
        // first grep hit of each method NAME, which is a CALL SITE (:233, :248), not the declaration
        // (:356, :369). (2) This file DOES open the local store - SqliteCipherHelper at :72, :279,
        // :417 - but that is its OTHER job: it reads monitored servers, diffs, then persists locally.
        // A file containing local-store calls does not make a given METHOD local-store. Reasoning
        // per-FILE about a per-METHOD property is what produced the false alarm.
        // Data/Services/ChangedObjectsService.cs:362 (cmd, DatabaseListSql) on the connection at
        // :360, new SqlConnection(masterConnectionString) - a MONITORED string passed in as a
        // parameter. DatabaseListSql is declared at :152 and its first verb is SELECT:
        // "SELECT name FROM sys.databases WHERE database_id > 4 ... HAS_DBACCESS(name) = 1".
        // READ + MONITORED: fix OWED, sweep's.
        "SQLTriage.Data.Services.ChangedObjectsService.ListAccessibleDatabasesAsync sites=1 prefixes=0",
        // ⚠ ADDED 2026-09-16, same lane, same discovery. Data/Services/ChangedObjectsService.cs:382
        // (cmd, InventorySql) on the connection at :380, which is new SqlConnection(builder.Connection
        // String) where the builder at :374 re-points masterConnectionString at the target database -
        // the mechanism the file's own doc comment at :206 already describes. InventorySql is declared
        // at :165 and its first verb is SELECT (schema/object catalog). READ + MONITORED: fix OWED,
        // sweep's.
        "SQLTriage.Data.Services.ChangedObjectsService.CollectDatabaseInventoryAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.CodeHotspotsCacheService.CaptureSnapshotAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.CodeHotspotsService.GetDatabasesAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.CodeHotspotsService.GetObjectsAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.CodeHotspotsService.GetStatementsAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.ConnectionHealthService.CheckServerAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.ConsolidationAnalysisService.GatherWorkloadTelemetryAsync sites=3 prefixes=0",
        "SQLTriage.Data.Services.ConsolidationAnalysisService.ProbeServerAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Discovery.SqlTopologyProbe.ProbeAsync sites=2 prefixes=0",
        "SQLTriage.Data.Services.DiskIoService.GetInventoryAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.DiskIoService.SampleVfsAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.IndexAnalysisService.ReadDatabaseRankingAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.IndexAnalysisService.ReadFragmentedIndexesAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.IndexAnalysisService.ReadMissingIndexesAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.IndexAnalysisService.ReadUnusedIndexesAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.IndexAnalysisService.ReadUsageWindowAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Jobs.AgRoleResolverService.GetReplicaRolesAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Jobs.JobInventoryService.GetJobsAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Licensing.InstanceFingerprintProbe.TryProbeAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.LicensingEstimator.ProbeAgReplicasAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.LicensingEstimator.ProbeServerAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.MaintenanceScriptService.GenerateCheckDbScriptAsync sites=1 prefixes=0",
        // ⚠ COUNT CORRECTED 2 -> 3, 2026-09-16 (a peer session). Hand-counted in source and it
        // agrees with the analyser: MaintenanceScriptService.cs:94 (SERVERPROPERTY EngineEdition),
        // :104 (SELECT name FROM sys.databases) and :140 (sys.dm_db_index_physical_stats LIMITED).
        // All three are READS. The connection is GetConnectionString(serverName) -> :43
        // _connectionManager.GetEnabledConnections() — MONITORED, no local-store path. The ALTER
        // INDEX text this method assembles is RETURNED as a script, never executed here, so the
        // method is a read lane despite what its output says. READ + MONITORED: fix OWED, sweep's.
        "SQLTriage.Data.Services.MaintenanceScriptService.GenerateIndexMaintenanceScriptAsync sites=3 prefixes=0",
        // ⚠ COUNT CORRECTED 1 -> 2, 2026-09-16 (a peer session). MaintenanceScriptService.cs:251
        // (SELECT name FROM sys.databases) and :279 (sys.stats / sys.partitions). Both READS, same
        // GetEnabledConnections source as the index method above — MONITORED. Fix OWED, sweep's.
        "SQLTriage.Data.Services.MaintenanceScriptService.GenerateStatisticsUpdateScriptAsync sites=2 prefixes=0",
        "SQLTriage.Data.Services.MissingIndexService.GetCandidatesAsync sites=1 prefixes=0",
        // ⚠ ADDED 2026-09-16 (a peer session). PRESENT ONLY ON THE full+private AXIS.
        // buildprofile.targets:352-354 Compile-Removes Data/Services/PerformanceReport/**/*.cs unless
        // -p:SQLTriagePrivate=true, and this list is NOT profile-aware (MeasuredCensusFloor is; see
        // IsFullProfile). So on full and on community the method is absent from the assembly and this
        // row is INERT BY CONSTRUCTION — neither NewDebt nor StaleEntries can ever name it there.
        // That is exactly the orphan shape the failure text warns about, accepted deliberately
        // because the only alternative is leaving the full+private axis RED. It was NOT introduced by
        // the widening: PROVED 2026-09-16 by running the census at f137afc's own tree, where this row
        // is already in the newDebt set on full+private.
        // READ (SELECT SERVERPROPERTY('ServerName'), PerformanceReportComposer.cs:217) on a
        // connection opened at :118 from ComposeAsync's ServerConnection parameter — MONITORED.
        "SQLTriage.Data.Services.PerformanceReport.PerformanceReportComposer.ResolveCanonicalAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Portal.BackupCollector.QueryBackupSnapshot sites=1 prefixes=0",
        "SQLTriage.Data.Services.Portal.CapacityCollector.QueryDiskSnapshot sites=1 prefixes=0",
        "SQLTriage.Data.Services.Portal.IdentityManifestExporter.ReadIdentitiesAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.PowerEstimateService.ProbeAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Remediation.DbatoolsRemediationExecutor.ExecuteMaintenanceSolutionInstallAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Remediation.DbatoolsRemediationExecutor.ExecuteNonQueryAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Remediation.DbatoolsRemediationExecutor.RowsAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Remediation.DbatoolsRemediationExecutor.ScalarAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Remediation.DeferredVerificationService.VerifyNowAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.Remediation.ServerSizingService.ReadAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.RestoreVerifyService.QueryMostRecentFullTargets sites=1 prefixes=0",
        // ✅ MEASURED-VISIBLE 2026-09-16 (lane census-blind-to-interface-dispatch).
        // NEWLY VISIBLE, NOT NEW CODE — the INTERFACE half, and the instance the opening brief did
        // not name. Counts MEASURED and CONFIRMED in RELEASE on both
        // axes at 97f158f. ★ The confirmation is structural, not a second opinion: NewDebt keys on
        // the WHOLE row text including sites= and prefixes=, so a wrong count cannot pass — it
        // presents as new debt and goes RED. Both axes were GREEN, so every count here was read
        // off the product assembly and then checked by the assertion.
        // ⚠ PRESENT ONLY ON THE FULL AXIS. buildprofile.targets:220 Compile-Removes
        // Data/Services/RiskReport/**/*.cs when SQLTExcludeDevTools is true, and this list is NOT
        // profile-aware (MeasuredCensusFloor is; see IsFullProfile). On community these two rows are
        // INERT BY CONSTRUCTION — the same accepted orphan shape as the PerformanceReport row below.
        // Data/Services/RiskReport/LiveRiskAssessmentSource.cs: the connection is opened once at :38
        // by LoadAsync (:30) through IDbConnectionFactory.CreateConnectionAsync() and handed to both
        // methods, which build one command each — LoadHeaderAsync site :60, LoadRowsAsync site :91.
        // LoadAsync itself builds no command and gets no row.
        // READ + MONITORED on both: SELECT TOP 1 over [SQLDBA.ORG].[dbo].[Domains_Details_Reporting]
        // (:54-58) and SELECT over [SQLDBA.ORG].[dbo].[003_Checks_ConsultantTasks] (:81-87), both
        // parameterised on @domain. The SQLDBA.ORG repository sits on a monitored server, and the
        // factory has no local-store branch. ⚠ THE WHOLE RENTAL IS TWO READS AND NO WRITE, so the
        // session form would cover both with one call at :38. Fix OWED, sweep's.
        "SQLTriage.Data.Services.RiskReport.LiveRiskAssessmentSource.LoadHeaderAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.RiskReport.LiveRiskAssessmentSource.LoadRowsAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.ScheduledTaskEngine.ExecuteOnServerAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.ScheduledTaskEngine.RunSingleVerifyAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.ServerConfigBaselineService.FetchLiveConfigAsync sites=4 prefixes=0",
        // ⚠ ADDED 2026-09-16 (lane s1-census-blind-to-the-factory, session Customer update path lane [8a308e]). NEWLY VISIBLE, not new code.
        // Data/Services/ServerConfigScriptService.cs:184 (cmd, inside the per-batch loop) on the
        // connection at :156, _factory.CreateConnection("master"). MONITORED.
        // ⚠⚠ THIS ONE IS A WRITE, SO NO READ UNCOMMITTED IS OWED HERE — on a write it would be a
        // defect, not a fix. The CommandText is one GO-split batch of ConfigScripts/Server
        // Configuration and Hardening.sql (250,968 bytes), read at :141 and rewritten at :144 by
        // RewriteChangeControlMode: @ForChangeControl 1 = preview, 0 = APPLY. ⚠ An earlier draft
        // of this comment claimed "165 lines match sp_configure/RECONFIGURE/ALTER/CREATE"; the cold
        // gate got 272 and the pattern had not been recorded, so that figure is WITHDRAWN as
        // unreproducible. What is exact: the file is 250,968 bytes, and :487-499 is EXEC
        // sp_configure followed by RECONFIGURE. A count whose pattern is unrecorded is not a
        // measurement. And ONE RENTAL RUNS EVERY BATCH on the one connection, so a read's
        // isolation change would still be in force for the applying batches — that is point (1)'s
        // whole-rental question, and the answer is WRITE.
        // ⚠ BuildPrefix CANNOT BE USED HERE EVEN WITH readUncommitted:false. CORRECTED by the cold
        // gate 2026-09-16 using the app's OWN splitter regex (Data/Services/
        // ServerConfigScriptService.cs:1074 - a line-anchored bare GO with optional surrounding
        // whitespace and an optional trailing line comment. (The regex is described rather than
        // quoted here on purpose: quoting it put a literal TAB and CARRIAGE RETURN into this
        // comment, and the CR split the line so the rest of it fell outside the // and broke the
        // build. A regex with escapes is an injection hazard against the file you are writing.)
        // The script has 5
        // batches, of which TWO begin with CREATE PROCEDURE — not the 4 an earlier draft claimed.
        // ⚠⚠ AND THE MATERIAL DETAIL THAT DRAFT OMITTED: both of those two carry
        // SQLTRIAGE_APPLY_ONLY_BATCH, which RunAsync:177 SKIPS when apply == false. So on PREVIEW
        // the batches that forbid BuildPrefix never run at all, and the human making this call
        // needs that — the bar applies to the APPLY path, not to preview. A leading
        // CREATE/ALTER PROCEDURE|VIEW|TRIGGER|FUNCTION must be the first statement in its batch — SqlSessionSafety.BuildPrefix's own remarks say exactly that and name ApplyAsync
        // as the form for this shape. ApplyAsync(readUncommitted: false) would still deliver SET
        // ANSI_WARNINGS ON and SET LOCK_TIMEOUT, so a fix is not impossible — but the 5000 ms
        // DefaultLockTimeoutMs is wrong for a RECONFIGURE/DDL lane (ruling R1 put DDL at 30-60 s)
        // and choosing that value is not this lane's call. UNKNOWN — needs a human.
        "SQLTriage.Data.Services.ServerConfigScriptService.RunAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.ServerConfigScriptService.RunScriptCoreAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.SqlAssessmentService.GetServerNameAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.WaitStatsService.GetSnapshotAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.XEventService.CreateSessionAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.XEventService.DropSessionAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.XEventService.GetAllSessionsAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.XEventService.GetSessionsAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.XEventService.SetStartupStateAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.XEventService.StartSessionAsync sites=1 prefixes=0",
        "SQLTriage.Data.Services.XEventService.StopSessionAsync sites=1 prefixes=0",
        // ⚠ ADDED 2026-09-16 (lane s1-census-blind-to-the-factory, session Customer update path lane [8a308e]). NEWLY VISIBLE, not new code.
        // Data/SessionDataService.cs:148 (cmd) on the connection at :145,
        // CreateConnection("master"). CommandText is BuildLiveSessionsQuery (declared :34), a
        // composed SELECT over sys.dm_exec_sessions / sys.dm_exec_requests with a parameterised
        // @SearchText. READ + MONITORED: fix OWED, sweep's — and it is an R3 case, because the
        // query already carries its own WITH (NOLOCK) hints (:71-72, and :42 conditionally) that
        // the preamble is meant to replace.
        // ⚠ THE WHOLE-RENTAL QUESTION, ASKED AND ANSWERED: this class DOES write — KILL {spid} at
        // :353, new SqlCommand($"KILL {spid}", sqlConn) — but that is a different method on a
        // different connection, and FetchSessionsAsync's rental is one command disposed at method
        // exit. No write shares this session.
        "SQLTriage.Data.SessionDataService.FetchSessionsAsync sites=1 prefixes=0",
        // ✅ MEASURED-VISIBLE 2026-09-16 (lane census-blind-to-interface-dispatch).
        // NEWLY VISIBLE, NOT NEW CODE — the INTERFACE half, and the three siblings of the row above
        // that made this lane provable: same file, same IDbConnectionFactory field (:25), and until
        // today only the one that CASTS to the concrete type at :144 was counted. Counts MEASURED and CONFIRMED in RELEASE on both
        // axes at 97f158f. ★ The confirmation is structural, not a second opinion: NewDebt keys on
        // the WHOLE row text including sites= and prefixes=, so a wrong count cannot pass — it
        // presents as new debt and goes RED. Both axes were GREEN, so every count here was read
        // off the product assembly and then checked by the assertion.
        //
        // GetBlockingChainAsync (:195): site :207, a SELECT over sys.dm_os_waiting_tasks joined to
        // sys.dm_exec_sessions / sys.dm_exec_requests, on the connection at :200. READ + MONITORED.
        // THE WHOLE RENTAL is this one command, disposed at method exit. Fix OWED, sweep's.
        "SQLTriage.Data.SessionDataService.GetBlockingChainAsync sites=1 prefixes=0",
        // GetQueryPlanAsync (:300): site :305, LivePlanForSpidSql (declared :289) — SELECT
        // CONVERT(NVARCHAR(MAX), qp.query_plan) FROM sys.dm_exec_requests WITH (NOLOCK) CROSS APPLY
        // sys.dm_exec_query_plan, parameterised on @Spid. Connection at :302. READ + MONITORED, one
        // command per rental. ⚠ An R3 case: the query already carries its own WITH (NOLOCK), which
        // is what the preamble is meant to replace. Fix OWED, sweep's.
        "SQLTriage.Data.SessionDataService.GetQueryPlanAsync sites=1 prefixes=0",
        // ⚠⚠ KillSessionAsync (:348) IS THE ONE THE BLIND SPOT WAS WORST ABOUT, AND IT IS NOT A READ
        // LANE. sites=2 is ONE method with TWO commands on ONE rental, not a method that grew:
        // :353 is new SqlCommand($"KILL {spid}", sqlConn) — a WRITE, an interpolated SPID into the
        // text (the parameter cannot be one for KILL) — and :358 is new SqlCommand(SessionAfterKillSql,
        // sqlConn), the SELECT that re-reads the session to report what actually happened. Both run
        // on the connection opened at :350. MONITORED.
        // ⚠⚠ SO THE WHOLE-RENTAL ANSWER IS WRITE, AND THIS ROW MUST NOT BE "FIXED" WITH THE SESSION
        // FORM. The preamble is session-scoped, so a READ UNCOMMITTED applied for the :358 re-read
        // would still be in force for the KILL at :353 — the exact defect this census exists to
        // prevent, and the reason to ask about the rental rather than the command. If anything is
        // owed here it is ApplyAsync(readUncommitted: false) for the LOCK_TIMEOUT alone, and the
        // value for a KILL lane is a human's call (ruling R1 put DDL at 30-60 s; this method already
        // sets CommandTimeout 30 and 10). UNKNOWN — needs a human.
        "SQLTriage.Data.SessionDataService.KillSessionAsync sites=2 prefixes=0",
        "SQLTriage.Data.SqlConnectionPoolService.TryResetSessionAsync sites=1 prefixes=0",
        "SQLTriage.Pages.AgentJobTimeline.FetchJobHistoryAsync sites=1 prefixes=0",
        "SQLTriage.Pages.Alerts.TestQuery sites=1 prefixes=0",
        // ⚠ ADDED 2026-09-16 (lane s1-census-blind-to-the-factory, session Customer update path lane [8a308e]). NEWLY VISIBLE, not new code.
        // Pages/BestPractice.razor:324 (cmd) on the connection at :320,
        // factory!.CreateConnection("master"). MONITORED.
        // ⚠⚠ A WRITE LANE, SO NO READ UNCOMMITTED IS OWED. The CommandText is a whole .sql file
        // out of BPScripts/ (:312, BPService.GetScriptContent) with {{param}} placeholders
        // string-replaced at :313-317. ⚠ CORRECTED by the cold gate 2026-09-16: the app's own
        // enumerator (Data/BPScriptService.cs:75, Directory.GetFiles(_scriptsPath, "*.sql")) sees
        // TWELVE .sql files, not the 15 an earlier draft claimed — 15 is every file in BPScripts/
        // (12 .sql + 2 .ps1 + ruleset.json), a population the app never reads. The companion claim
        // that "10 match a write verb" is WITHDRAWN: it reproduces with no recorded pattern. The
        // hand-verified citations carry the verdict on their own — "01.
        // MaintenanceSolution.sql":78 CREATE TABLE [dbo].[CommandLog] and :9460 EXECUTE
        // msdb.dbo.sp_add_job (⚠ :61 was also cited in that draft and is DROPPED here: it is
        // CREATE TABLE #Config, a TEMP table, which this same lane treats as "read in effect" for
        // the dashboard panels — citing it as write evidence contradicted that); and
        // "05. Database_Mail_Configuration.sql":13 and :17 RECONFIGURE. The lane is MIXED (some
        // scripts are pure diagnostics) and BPScriptService.SaveScriptContent writes user edits
        // back into that folder, so the CONTENT is user-supplied and this site can never promise a
        // read. Same BuildPrefix bar as ServerConfigScriptService.RunAsync above: CREATE PROCEDURE
        // must be first in its batch. UNKNOWN — needs a human, and it is the same decision.
        "SQLTriage.Pages.BestPractice.ExecuteScript sites=1 prefixes=0",
        "SQLTriage.Pages.CapacityPlanning.LoadDiskForecastsFromServer sites=1 prefixes=0",
        "SQLTriage.Pages.EnvironmentView.ScanServerAsync sites=1 prefixes=0",
        "SQLTriage.Pages.ReplicationMap.ScanAsync sites=1 prefixes=0",
        "SQLTriage.Pages.ScheduledTasks.CheckOlaJobsAsync sites=1 prefixes=0",
        "SQLTriage.Pages.ScheduledTasks.DeployOlaMaintenanceAsync sites=1 prefixes=0",
        "SQLTriage.Pages.Servers.CheckPerformanceMonitorDatabase sites=1 prefixes=0",
        "SQLTriage.Pages.Servers.CheckSqlWatchDatabase sites=1 prefixes=0",
    };

    /// <summary>
    /// THE CORE PIN, as a RATCHET. Every method in the product assembly that builds a SQL command a
    /// monitored-server connection can reach must reference <see cref="SqlSessionSafety"/> — unless it
    /// is on the frozen <see cref="FrozenUnguarded"/> debt list. A NEW unguarded site goes red with
    /// nothing for anyone to remember; the enumeration is from the code, so there is no list of
    /// SITES to maintain, only a shrinking list of known exceptions.
    ///
    /// <para>Asserted in BOTH directions on purpose. Forward stops the debt growing. Backward — an
    /// entry that is present in this build and now GUARDED must be removed — stops the list rotting
    /// into a permanent blanket that silently forgives a site somebody already fixed.</para>
    ///
    /// <para>The whole distribution is printed and asserted against, never filtered and counted: a
    /// census that only counts its own matches reports 0 when the instrument is dead, and 0 reads as
    /// perfect health.</para>
    /// </summary>
    [Fact]
    public void No_new_monitored_server_query_site_may_skip_session_safety()
    {
        // Refuse to measure a configuration these numbers were not frozen in, FIRST — so a Debug
        // run fails here rather than in the count assertions below, whose message names the Debug
        // counts and tells the reader to replace the frozen rows with them.
        RequireTheBuildConfigurationTheseNumbersWereFrozenIn();

        var census = SessionSafetyAnalyzer.Census(Product);
        var frozen = new HashSet<string>(FrozenUnguarded, StringComparer.Ordinal);

        _out.WriteLine($"monitored-server command sites, whole product assembly: {census.Count} methods, "
                     + $"{census.Sum(c => c.Sites)} sites, {census.Count(c => c.Unguarded)} unguarded, "
                     + $"{census.Count(c => c.Direct)} visible to the pre-widening census");
        foreach (var c in census) _out.WriteLine("  " + c);
        _out.WriteLine($"frozen debt entries: {frozen.Count}");

        // HAYSTACK BEFORE NEEDLE. If the analyser found nothing at all it is broken, not the code
        // clean — assert the subject exists before asserting anything about it.
        census.Should().NotBeEmpty(
            "the analyser must find the monitored-server command sites before its silence means anything");
        census.Count.Should().BeGreaterThanOrEqualTo(MeasuredCensusFloor,
            "the census reached this many methods when the analyser was widened on 2026-09-15; a smaller "
            + "population means the walk re-narrowed, not that the sites went away");

        // ── THE WITHIN-FILE CONTROL, RESTATED 2026-09-16 — and it HAD to be restated, because the
        //    dispatch fix DISSOLVED the old one. Until that lane the discriminating control was
        //    Data/SessionDataService.cs: one of its four methods was in this census
        //    (FetchSessionsAsync, which casts to the concrete factory at :144) and three were not
        //    (they call IDbConnectionFactory). Making all four visible is the whole point of the
        //    lane — and it leaves that file with nothing left to discriminate. Deleting the control
        //    instead of restating it is the failure this repo already has a lesson for: a retraction
        //    needs a scope as precisely as a claim.
        //
        //    BenchmarkService is the honest replacement, because BOTH halves survive the fix. It
        //    reaches a MONITORED connection through IDbConnectionFactory.CreateConnection() at
        //    Data/Services/BenchmarkService.cs:54 and builds five DMV commands on it — those must be
        //    PRESENT. It ALSO runs four commands on _cacheStore.CreateExternalConnection(), the
        //    app's own encrypted SQLite store (sites :281, :337, :360, :397;
        //    Data/Caching/SqliteCacheStore.cs:182 opens it) — those must stay ABSENT. Same file,
        //    same class, same profile; only the connection's ORIGIN differs.
        //
        //    ⚠ THIS IS A SPECIMEN CONTROL, NOT THE GUARANTEE. The guarantee is the census above,
        //    which enumerates from product IL. Naming four methods cannot prove a set of 103; it can
        //    only fail loudly when the instrument stops discriminating in either direction — blind
        //    again to a monitored connection, or reporting the app's own store.
        //    ⚠ THE PRESENCE CHECK IS AN ASSERTION, NOT A SKIP. BenchmarkService is named nowhere in
        //    buildprofile.targets (grepped at 97f158f), so it is in BOTH profile assemblies and a
        //    missing type is itself the finding — a control whose subject was silently
        //    Compile-Removed reads exactly like a control that passed.
        const string BenchmarkOwner = "SQLTriage.Data.Services.BenchmarkService";
        var benchmarkRows = census.Where(c => c.Owner == BenchmarkOwner)
                                  .Select(c => c.Method)
                                  .OrderBy(s => s, StringComparer.Ordinal).ToList();
        var benchmarkTypeIsHere = Product.GetType(BenchmarkOwner, throwOnError: false) != null;
        var benchmarkSeen = benchmarkRows.Count == 0 ? "(no rows)" : string.Join(", ", benchmarkRows);
        _out.WriteLine($"within-file control {BenchmarkOwner}: type present={benchmarkTypeIsHere}, "
                     + $"census rows=[{benchmarkSeen}]");

        benchmarkTypeIsHere.Should().BeTrue(
            "the within-file control needs its subject in the assembly under test. BenchmarkService "
            + "is not named in buildprofile.targets, so it should be in both the full and the "
            + "community build. WHAT TO CHECK: whether it was profile-gated, renamed or deleted — and "
            + "then MOVE this control to another type that has both a monitored and a local-store "
            + "command in one class, rather than deleting it. Do not weaken it to nothing.");

        benchmarkRows.Contains("RunCpuIntegerBenchmarkAsync").Should().BeTrue(
            "rows seen for this type: [" + benchmarkSeen + "]. "
            + "THE POSITIVE HALF. This method gets its connection from the interface member "
            + "IDbConnectionFactory.CreateConnection(), whose declaration has NO IL — before "
            + "2026-09-16 the analyser consulted only the body a metadata token names, so the "
            + "implementation's return-taint never reached here and every one of this class's five "
            + "DMV reads was invisible. If it is missing, the dispatch edge in SessionSafetyAnalyzer "
            + "has stopped working and the census has quietly re-narrowed. WHAT TO CHECK: the four "
            + "compiled specimens named BuildsOnConnectionFrom* in "
            + nameof(The_analyser_flags_reached_sites_and_clears_guarded_and_local_ones) + " first — "
            + "they say whether the edge or the product changed.");

        benchmarkRows.Contains("EnsureBenchmarkTables").Should().BeFalse(
            "rows seen for this type: [" + benchmarkSeen + "]. "
            + "THE NEGATIVE HALF, AND THE FLOOD ALARM. This method runs CREATE TABLE IF NOT EXISTS on "
            + "_cacheStore.CreateExternalConnection() — the app's OWN encrypted SQLite store. If it "
            + "appears, taint has reached the local store again and the census is on its way back to "
            + "the 202-method shape measured on 2026-09-15. ⚠ DO NOT wrap it in READ UNCOMMITTED and "
            + "do NOT freeze it: read which connection it is really on first.");
        benchmarkRows.Contains("StoreBenchmarkResultsToDedicatedTablesAsync").Should().BeFalse(
            "same local store, same alarm: INSERT INTO benchmark_runs on the app's own file");
        benchmarkRows.Contains("GetLatestRuns").Should().BeFalse(
            "same local store, same alarm: a SELECT over the app's own benchmark_runs table");

        // ⚠ THE FAILURE TEXT BELOW MUST BE SAFE TO OBEY. It names what to CHECK; it does not prescribe
        // a fix. The previous wording said "a read issued against a client's production server must
        // carry the S-1 session-safety preamble", which asserted two things this census cannot
        // establish — that the command is a READ, and that its target is a monitored server — and then
        // told the reader to add READ UNCOMMITTED on that basis. Both are wrong for real entries in this
        // very list: 44 of the frozen 86 have never been classified read-vs-write and several are
        // demonstrably writes (XEventService issues CREATE/ALTER/DROP EVENT SESSION), and the walk
        // over-approximates, so a reported site may be on the app's own local store. A reader obeying
        // the old text would wrap a WRITE, or a LOCAL write, in READ UNCOMMITTED — the exact defect this
        // census exists to prevent. Caught 2026-09-15 by a peer session reviewing this lane's own output.
        var newDebt = NewDebt(census, FrozenUnguarded);
        newDebt.Should().BeEmpty(
            "SITES: " + string.Join(", ", newDebt) + " — the analyser's taint walk says a "
            + "MONITORED-SERVER connection string can reach each of these command builds, and the "
            + "method does not reference SqlSessionSafety on EVERY site it builds (compare the "
            + "prefixes= and sites= counts in each key above; a partially guarded method is reported "
            + "too, and it does reference the class). That is a LEAD, not a verdict — establish two "
            + "things before changing anything. (1) IS IT A READ? READ UNCOMMITTED is a safety control "
            + "for reads; on a WRITE it is a DEFECT, not a fix. And the preamble is SESSION-scoped in "
            + "both forms, so on a connection that later writes, a read's READ UNCOMMITTED is still in "
            + "force for those writes — ask about the whole rental, not the one command. (2) IS THE "
            + "TARGET REALLY A MONITORED SERVER? This analyser over-approximates — confirm at the call "
            + "sites which connection the command is actually built on, because the app's own local "
            + "store can reach this list. The boundaries are listed on SessionSafetyAnalyzer itself. "
            + "THEN: if it is a read against a monitored server, apply SqlSessionSafety (DefaultPrefix/"
            + "BuildPrefix on the command text, or ApplyAsync on the connection). If it is a write, or "
            + "local, or you cannot tell yet, put it in " + nameof(FrozenUnguarded) + " with a comment "
            + "saying which, dated and initialled — an honest entry beats a wrong fix. ⚠ If the method "
            + "is ALREADY frozen at a different count, REPLACE that row; do not add a second. The stale "
            + "check matches on the METHOD, so an orphaned row is never reported and silently forgives "
            + "whatever count it names. A changed site count on an already-frozen method appears here "
            + "by design.");

        // Backward: a frozen entry present in this build that is now guarded must leave the list.
        var stale = StaleEntries(census, FrozenUnguarded);
        stale.Should().BeEmpty(
            "these sites now carry session safety, so their entries must be DELETED from "
            + nameof(FrozenUnguarded) + " — a debt list that keeps forgiving what is already fixed "
            + "stops being a ratchet: " + string.Join(", ", stale));
    }

    /// <summary>
    /// Debt that is not on the frozen list. Keyed on the FULL counted key, so growing an already-frozen
    /// method's site count reads as new debt — which is the whole reason the counts are in the key.
    /// </summary>
    private static List<string> NewDebt(
        IEnumerable<SessionSafetyAnalyzer.MethodCensus> census, IReadOnlyCollection<string> frozen)
    {
        var set = new HashSet<string>(frozen, StringComparer.Ordinal);
        return census.Where(c => c.Unguarded && !set.Contains(c.Key))
                     .Select(c => c.Key).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Frozen entries whose method is now GUARDED, and which must therefore be deleted from the list.
    ///
    /// <para>⚠ <b>THIS MATCHES ON THE METHOD, NOT ON THE COUNTED KEY, AND THAT ASYMMETRY IS THE POINT.</b>
    /// The two halves of the ratchet need opposite things from the key. Growing debt needs the counts, or
    /// a frozen method silently takes on new unguarded reads. Clearing debt must NOT use them, because
    /// fixing a site CHANGES them: the recommended prefix form takes an entry from
    /// <c>sites=1 prefixes=0</c> to <c>sites=1 prefixes=1</c>, and matching on the full key would then
    /// find nothing, leave the entry orphaned, and pass GREEN.</para>
    ///
    /// <para>That is not hypothetical. The cold gate PROVED it on 2026-09-15 against the first build of
    /// this lane: applying <c>SqlSessionSafety.DefaultPrefix</c> to the frozen
    /// <c>WaitStatsService.GetSnapshotAsync</c> left all three tests GREEN with the stale entry still in
    /// the list. Worse, an orphan is a live blanket for the REVERTED state — remove the prefix later and
    /// the row returns to <c>sites=1 prefixes=0</c>, which is still frozen, so a real regression passes.
    /// 33 of the 43 classified entries are recommended for the prefix form, so this would have fired on
    /// the very first fix of the sweep this lane exists to unblock.</para>
    ///
    /// <para>The invariant, stated so the next change to this key cannot quietly break it again: <b>the
    /// list must go red when debt GROWS and when a listed entry is FIXED, under EITHER fix form.</b> It
    /// is pinned by <see cref="The_debt_list_goes_red_when_a_frozen_entry_is_fixed_by_either_form"/>.</para>
    /// </summary>
    private static List<string> StaleEntries(
        IEnumerable<SessionSafetyAnalyzer.MethodCensus> census, IReadOnlyCollection<string> frozen)
        => census.Where(c => !c.Unguarded)
                 .SelectMany(c => frozen.Where(
                     f => f.StartsWith(c.Owner + "." + c.Method + " ", StringComparison.Ordinal)))
                 .Distinct(StringComparer.Ordinal)
                 .OrderBy(s => s, StringComparer.Ordinal)
                 .ToList();

    /// <summary>
    /// THE CONTROL FOR THE RATCHET ITSELF, over synthetic rows so it cannot be moved by product changes.
    /// Both halves, both fix forms, plus the two ways a naive implementation goes wrong: matching the
    /// counted key on the way out (misses a prefix fix), and matching a bare name prefix (would let a
    /// different method whose name merely starts the same delete this one's entry).
    /// </summary>
    [Fact]
    public void The_debt_list_goes_red_when_a_frozen_entry_is_fixed_by_either_form()
    {
        // NOT guarded by RequireTheBuildConfigurationTheseNumbersWereFrozenIn, deliberately: every
        // row below is SYNTHETIC. This test never loads the product assembly and never compares
        // against a frozen measurement — it pins the ratchet's own logic over hand-built rows, which
        // are identical in every configuration. A guard here would make a Debug run redder without
        // making it truer.
        const string Owner = "Fake.Service";
        const string Method = "ReadAsync";
        var frozen = new[] { $"{Owner}.{Method} sites=1 prefixes=0" };

        static SessionSafetyAnalyzer.MethodCensus Row(string method, int sites, int prefixes, bool apply)
            => new(Owner, method, sites, prefixes, apply, Direct: true);

        // Untouched: the census matches the frozen entry exactly. Silent, both ways.
        NewDebt(new[] { Row(Method, 1, 0, false) }, frozen).Should().BeEmpty();
        StaleEntries(new[] { Row(Method, 1, 0, false) }, frozen).Should().BeEmpty();

        // FIXED with the PREFIX form — prefixes 0 -> 1, so the counted key MOVED. This is the case the
        // cold gate broke, and the one 33 of the 43 classified entries are recommended to take.
        StaleEntries(new[] { Row(Method, 1, 1, false) }, frozen).Should().ContainSingle()
            .Which.Should().Be(frozen[0], "the orphaned entry must be named so it can be deleted");
        NewDebt(new[] { Row(Method, 1, 1, false) }, frozen).Should().BeEmpty(
            "a fixed site is not new debt");

        // FIXED with the ApplyAsync form — the counted key does NOT move. Must still go red.
        StaleEntries(new[] { Row(Method, 1, 0, true) }, frozen).Should().ContainSingle();

        // STILL BROKEN and GROWN — new debt, not a stale entry. The forward half.
        NewDebt(new[] { Row(Method, 2, 0, false) }, frozen).Should().ContainSingle()
            .Which.Should().Be($"{Owner}.{Method} sites=2 prefixes=0");
        StaleEntries(new[] { Row(Method, 2, 0, false) }, frozen).Should().BeEmpty();

        // A DIFFERENT method whose name merely starts with the frozen one must not clear it — the
        // trailing space in the match is what stops ReadAsyncCore from deleting ReadAsync's entry.
        StaleEntries(new[] { Row(Method + "Core", 1, 1, false) }, frozen).Should().BeEmpty();
    }

    /// <summary>
    /// The alert loop specifically — the lane this guard was built for. Separated from the
    /// assembly-wide pin so a regression here is named in the failure rather than buried in a list,
    /// and so the site FLOOR is asserted where the six known sites actually live.
    /// </summary>
    [Fact]
    public void The_alert_loop_still_has_its_query_sites_and_all_are_guarded()
    {
        // Guarded too, and NOT because a number moved today. MEASURED 2026-09-16 at b295c12: this
        // loop reports sites=6 prefixes=6 in BOTH configurations. The guard is here because this
        // test's verdict is a frozen count over PRODUCT IL, and product IL site counts are proved
        // configuration-sensitive in this very assembly (120 sites in Release, 117 in Debug). A
        // Debug pass here would be a green nobody measured, and a Debug failure would blame the
        // analyser ("the walk broke") or the alert loop itself for a codegen difference.
        RequireTheBuildConfigurationTheseNumbersWereFrozenIn();

        var census = SessionSafetyAnalyzer.Census(Product, IsAlertLoop);

        var sites = census.Sum(c => c.Sites);
        var prefixes = census.Sum(c => c.Prefixes);
        _out.WriteLine($"AlertEvaluationService: methods={census.Count} sites={sites} prefixes={prefixes}");
        foreach (var c in census) _out.WriteLine("  " + c);

        sites.Should().BeGreaterThanOrEqualTo(AlertLoopSiteFloor,
            "the alert loop's monitored-server command sites must still be visible to the analyser; "
            + "a count below the measured floor means the walk broke, not that the sites went away");

        census.Where(c => c.Unguarded).Should().BeEmpty(
            "every alert-loop read runs on a timer against a client's production server");
    }

    /// <summary>
    /// A CONTROL THAT CAN REPRODUCE, in BOTH directions. Absence of evidence is evidence of absence only
    /// once the instrument has been proved able to show a positive — and a WIDENED instrument needs the
    /// opposite proof too, that it has not simply started reporting everything. So this compiles six
    /// specimens into the test assembly and pins the analyser's verdict on each:
    ///
    /// <list type="number">
    /// <item><b>Positive, same method</b> — the shape the narrow census already caught.</item>
    /// <item><b>Negative, same method</b> — the same read carrying the prefix.</item>
    /// <item><b>Positive, HANDED DOWN</b> — the whole point of this lane. The method never mentions
    /// <c>GetConnectionString</c>; it receives an open monitored connection from a caller. This is
    /// <c>AccessSurfaceCollector</c>'s and <c>XEventService</c>'s shape, and it was invisible before
    /// 2026-09-15.</item>
    /// <item><b>Negative, handed down but guarded</b> — so "reached" alone is not enough to be flagged.</item>
    /// <item><b>Negative, one ApplyAsync covering TWO command sites</b> — the session form. Proved live
    /// against <c>.\new2022</c> on 2026-09-15: SET TRANSACTION ISOLATION LEVEL and SET LOCK_TIMEOUT
    /// persist across batches on a session. The pre-widening rule got this wrong and put a correct
    /// service on the debt list.</item>
    /// <item><b>Negative, ApplyAsync applied UPSTREAM</b> — guardedness travels the same edges the
    /// connection does, or widening would flood the list with correct code.</item>
    /// </list>
    ///
    /// <para>And the one that stops the widening from being vacuous: <see
    /// cref="SessionSafetyControls.LocalStoreSite"/> builds a command on a connection string that never
    /// came from a monitored-server builder, and must NOT appear in the census AT ALL. An analyser that
    /// reports every command site in the assembly would pass every other assertion here.</para>
    /// </summary>
    [Fact]
    public void The_analyser_flags_reached_sites_and_clears_guarded_and_local_ones()
    {
        // NOT guarded by RequireTheBuildConfigurationTheseNumbersWereFrozenIn, and that is a
        // judgement rather than an oversight. This test measures the TEST assembly's compiled
        // specimens, and every expectation here is read off the specimen SOURCE —
        // AppliesSessionThenBuildsTwo builds two commands, UnguardedSite carries no preamble — not
        // frozen from a measurement, so it must hold in ANY configuration. If this test ever goes
        // red in Debug that is a REAL finding, that the analyser's IL walk is codegen-fragile, and
        // a configuration refusal would relabel a genuine analyser defect as a bookkeeping
        // complaint and hide it. MEASURED 2026-09-16 at b295c12: GREEN on -c Debug and -c Release
        // alike, .trx-verified at EXECUTED=4 on both.
        var here = typeof(SessionSafetyControls).Assembly;
        var census = SessionSafetyAnalyzer.Census(here, t => t == typeof(SessionSafetyControls));

        _out.WriteLine($"controls: {census.Count} methods");
        foreach (var c in census) _out.WriteLine("  " + c);

        var byName = census.ToDictionary(c => c.Method, StringComparer.Ordinal);

        // The census must contain exactly the specimens that hold a monitored connection AND build a
        // command — named, not counted, so a miss says which one.
        byName.Keys.OrderBy(s => s, StringComparer.Ordinal).Should().Equal(
            nameof(SessionSafetyControls.AppliesSessionThenBuildsTwo),
            nameof(SessionSafetyControls.BuildsAfterUpstreamApply),
            nameof(SessionSafetyControls.BuildsAfterUpstreamApplyAsync),
            nameof(SessionSafetyControls.BuildsGuardedOnHandedConnection),
            nameof(SessionSafetyControls.BuildsOnConnectionFromADelegate),
            nameof(SessionSafetyControls.BuildsOnConnectionFromAnInterface),
            nameof(SessionSafetyControls.BuildsOnHandedConnection),
            nameof(SessionSafetyControls.GuardedSite),
            nameof(SessionSafetyControls.UnguardedSite));

        byName[nameof(SessionSafetyControls.UnguardedSite)].Unguarded.Should().BeTrue(
            "the analyser must be able to show a positive, or its silence proves nothing");
        byName[nameof(SessionSafetyControls.GuardedSite)].Unguarded.Should().BeFalse(
            "a site that applies the preamble must not be flagged");

        var handed = byName[nameof(SessionSafetyControls.BuildsOnHandedConnection)];
        handed.Unguarded.Should().BeTrue(
            "this is the shape the census could not see before 2026-09-15 — the connection is obtained "
            + "in another method and handed down, which is how AccessSurfaceCollector built six "
            + "unguarded commands and XEventService stayed invisible entirely");
        handed.Direct.Should().BeFalse(
            "it must be reached through the call graph, not by calling a connection-string builder "
            + "itself — otherwise this control is silently re-testing the narrow census");

        byName[nameof(SessionSafetyControls.BuildsGuardedOnHandedConnection)].Unguarded.Should().BeFalse(
            "being reachable by a monitored connection is not by itself a defect");

        // ── THE TWO EDGES ADDED 2026-09-16, each with a positive here and a negative below. A
        //    WIDENED instrument needs both halves: the positive proves the edge exists at all, the
        //    negative proves it has not started tainting everything it touches. These four are also
        //    the only thing that fails LOUDLY if Graph.DispatchTargets comes back EMPTY — every
        //    GetInterfaceMap call in BuildDispatchTargets sits in a catch, and a map that silently
        //    lost its edges makes the census SMALLER, which reads as progress.
        var viaInterface = byName[nameof(SessionSafetyControls.BuildsOnConnectionFromAnInterface)];
        viaInterface.Unguarded.Should().BeTrue(
            "the connection comes back from an INTERFACE member, whose declaration has no IL at all. "
            + "Before 2026-09-16 the analyser resolved a callee only by (Module, MetadataToken), so "
            + "the implementation's ReturnsTainted was never consulted and this entire shape was "
            + "invisible — which is why three of Data/SessionDataService.cs's four methods were "
            + "absent from the census while FetchSessionsAsync, which casts to the concrete factory "
            + "at :144, was counted. One of four visible, and the invisible one runs KILL {spid}");
        viaInterface.Direct.Should().BeFalse(
            "it must be reached through the DISPATCH edge, not by calling a connection-string builder "
            + "itself — otherwise this control is silently re-testing the path that already worked");

        var viaDelegate = byName[nameof(SessionSafetyControls.BuildsOnConnectionFromADelegate)];
        viaDelegate.Unguarded.Should().BeTrue(
            "the connection comes back through Func<SqlConnection>.Invoke, and the lambda that opens "
            + "it is reached ONLY by ldftn — which was not a call edge before 2026-09-16, so "
            + "SetReturnsTainted could never wake the method that built the delegate. All six of "
            + "Data/Services/AgentMailChainProbe.cs's connection sites are that shape, including the "
            + "one that sends mail");
        viaDelegate.Direct.Should().BeFalse(
            "the connection-string builder is called in the SOURCE half, HandsDownADelegate, not here");

        var applied = byName[nameof(SessionSafetyControls.AppliesSessionThenBuildsTwo)];
        applied.Sites.Should().BeGreaterThanOrEqualTo(2, "the specimen builds two commands");
        applied.Prefixes.Should().Be(0, "it carries no per-batch prefix — the session form is the guard");
        applied.Unguarded.Should().BeFalse(
            "ApplyAsync configures the SESSION, so one call covers every later command on that "
            + "connection; counting prefixes against sites here is what put the correct service "
            + "DiagnosticScriptRunner.ExecuteScriptAsync on the debt list before 2026-09-15");

        byName[nameof(SessionSafetyControls.BuildsAfterUpstreamApply)].Unguarded.Should().BeFalse(
            "the caller applied the preamble to this very connection before handing it down");

        byName[nameof(SessionSafetyControls.BuildsAfterUpstreamApplyAsync)].Unguarded.Should().BeFalse(
            "and the ASYNC hand-down must clear too — it is the only shape this product actually uses, "
            + "and the sync twin above passed while this one was flagged until 2026-09-15");

        // THE CONTROL AGAINST OVER-REPORTING. Everything above would still pass if the analyser simply
        // reported every command site it found.
        byName.Should().NotContainKey(nameof(SessionSafetyControls.LocalStoreSite),
            "a command built on a connection string that did not come from a monitored-server builder "
            + "is a read against the app's OWN store — it must never be wrapped in READ UNCOMMITTED, "
            + "and an analyser that reports it has stopped answering the S-1 question");

        // The same control, once per edge added 2026-09-16. Each of these is a SEPARATE
        // single-implementation interface / delegate whose only implementation opens a LITERAL local
        // connection string. Two separate interfaces and not one with two implementations, on
        // purpose: sharing one interface would correctly taint BOTH consumers through the union and
        // the negative half would then assert nothing.
        byName.Should().NotContainKey(nameof(SessionSafetyControls.BuildsOnConnectionFromALocalInterface),
            "THE FLOOD CONTROL FOR THE INTERFACE EDGE. The dispatch union must taint a caller only "
            + "when an implementation is GENUINELY MONITORED. If this appears, the analyser is "
            + "tainting by dispatch FORM rather than by origin — every interface-obtained connection "
            + "in the assembly would count, including the app's own store, which is the 202-method "
            + "flood measured on 2026-09-15");
        byName.Should().NotContainKey(nameof(SessionSafetyControls.BuildsOnConnectionFromALocalDelegate),
            "THE FLOOD CONTROL FOR THE DELEGATE EDGE, same reasoning: ldftn must push the target's "
            + "OWN return-taint, never merely the fact that a delegate was constructed");
    }
}

/// <summary>
/// Compiled controls for <see cref="SessionSafetyCensusTests"/>. NONE OF THESE METHODS IS EVER CALLED
/// AT RUNTIME — they exist so the analyser has known-bad and known-good specimens inside a real compiled
/// assembly. The calls between them are what create the call-graph edges the analyser follows, so the
/// source methods matter as much as the sites.
///
/// <para>Do not "fix" <see cref="UnguardedSite"/> or <see cref="BuildsOnHandedConnection"/>: their
/// missing preamble is the point, and making them safe blinds the only controls that prove the census
/// can fail. Do not add a monitored connection to <see cref="LocalStoreSite"/>: it is the only control
/// that proves the census has not started reporting everything.</para>
///
/// <para><b>⚠ THE FOUR <c>BuildsOnConnectionFrom*</c> SPECIMENS ARE A MATCHED SET, ADDED 2026-09-16
/// for the two dispatch edges, and removing any ONE of them makes the other three lie.</b> Each edge
/// has a POSITIVE (a monitored connection, reached through that edge, must be FLAGGED) and a NEGATIVE
/// (the identical shape over a literal LOCAL connection string must be ABSENT). Without the positive,
/// a dispatch edge that silently stopped working looks like a clean census — and the map it depends on
/// is built inside a <c>catch</c>, so it CAN come back empty. Without the negative, an edge that
/// taints by dispatch FORM rather than by ORIGIN looks like a working one, which is how this analyser
/// flooded to 202 methods on 2026-09-15.</para>
///
/// <para><b>⚠⚠ AND THE TWO INTERFACES BELOW ARE TOP-LEVEL ON PURPOSE. Nesting them inside this class
/// would silently break the control rather than fail.</b> The control test filters on
/// <c>t == typeof(SessionSafetyControls)</c> and the analyser maps a row's owner through its OUTERMOST
/// declaring type, so a nested implementation would be folded into THIS class — and the two
/// identically named <c>Open</c> methods would then collide in the census's group-by fold and throw in
/// its <c>ToDictionary</c>. Top-level keeps both implementations in the call graph, which is always
/// built over the whole assembly, while keeping them out of the reported rows.
/// They are TWO SEPARATE single-implementation interfaces and not one interface with two
/// implementations, also on purpose: with one shared interface the union over dispatch targets would
/// CORRECTLY taint both consumers, and the negative half would then be asserting nothing.</para>
/// </summary>
internal static class SessionSafetyControls
{
    /// <summary>Known-bad: a monitored-server read with no S-1 preamble. Must be FLAGGED.</summary>
    internal static SqlCommand UnguardedSite(ServerConnection connection, string serverName)
    {
        var connStr = connection.GetConnectionString(serverName, "master");
        return new SqlCommand("SELECT 1", new SqlConnection(connStr));
    }

    /// <summary>Known-good: the same read carrying the preamble. Must NOT be flagged.</summary>
    internal static SqlCommand GuardedSite(ServerConnection connection, string serverName)
    {
        var connStr = connection.GetConnectionString(serverName, "master");
        return new SqlCommand(SqlSessionSafety.DefaultPrefix + "SELECT 1", new SqlConnection(connStr));
    }

    /// <summary>
    /// The source half of the handed-down control. Builds no command itself — it opens the monitored
    /// connection and passes it on, which is exactly why the narrow census reported nothing for the
    /// method that does the work.
    /// </summary>
    internal static SqlCommand HandsDownConnection(ServerConnection connection, string serverName)
    {
        var connStr = connection.GetConnectionString(serverName, "master");
        using var conn = new SqlConnection(connStr);
        return BuildsOnHandedConnection(conn);
    }

    /// <summary>
    /// Known-bad, REACHED: never mentions a connection-string builder, and must still be FLAGGED.
    /// This is the lane's whole point.
    /// </summary>
    internal static SqlCommand BuildsOnHandedConnection(SqlConnection conn)
        => new SqlCommand("SELECT 1", conn);

    /// <summary>The source half of the guarded handed-down control.</summary>
    internal static SqlCommand HandsDownGuardedConnection(ServerConnection connection, string serverName)
    {
        var connStr = connection.GetConnectionStringForDashboard(serverName);
        using var conn = new SqlConnection(connStr);
        return BuildsGuardedOnHandedConnection(conn);
    }

    /// <summary>Known-good, REACHED: carries the prefix, so being reachable is not enough to flag it.</summary>
    internal static SqlCommand BuildsGuardedOnHandedConnection(SqlConnection conn)
        => new SqlCommand(SqlSessionSafety.DefaultPrefix + "SELECT 1", conn);

    /// <summary>
    /// Known-good: ONE <see cref="SqlSessionSafety.ApplyAsync"/> covering TWO command sites and no
    /// per-batch prefix at all. Must NOT be flagged — the SET statements are session-scoped.
    /// </summary>
    internal static async Task AppliesSessionThenBuildsTwo(ServerConnection connection, string serverName)
    {
        var connStr = connection.GetConnectionString(serverName, "master");
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await SqlSessionSafety.ApplyAsync(conn);

        using var first = conn.CreateCommand();
        first.CommandText = "SELECT 1";
        await first.ExecuteNonQueryAsync();

        using var second = conn.CreateCommand();
        second.CommandText = "SELECT 2";
        await second.ExecuteNonQueryAsync();
    }

    /// <summary>The source half of the upstream-apply control: applies the preamble, then hands down.</summary>
    internal static async Task AppliesThenHandsDown(ServerConnection connection, string serverName)
    {
        var connStr = connection.GetConnectionString(serverName, "master");
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await SqlSessionSafety.ApplyAsync(conn);
        BuildsAfterUpstreamApply(conn);
    }

    /// <summary>
    /// Known-good, REACHED: no preamble of its own, because its caller already configured the session
    /// on this very connection. Must NOT be flagged, or widening floods the debt list with correct code.
    /// </summary>
    internal static SqlCommand BuildsAfterUpstreamApply(SqlConnection conn)
        => new SqlCommand("SELECT 1", conn);

    /// <summary>The source half of the ASYNC upstream-apply control.</summary>
    internal static async Task AppliesThenHandsDownAsync(ServerConnection connection, string serverName)
    {
        var connStr = connection.GetConnectionString(serverName, "master");
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await SqlSessionSafety.ApplyAsync(conn);
        await BuildsAfterUpstreamApplyAsync(conn);
    }

    /// <summary>
    /// Known-good, REACHED, and ASYNC. ⚠ <b>The synchronous twin above is not enough, and believing it
    /// was hid a real defect.</b> Every hand-down in this product is async, and an async method's sites
    /// live in a compiler-generated <c>MoveNext</c> that no caller invokes with arguments in IL — so the
    /// route by which guardedness reaches the SYNC twin does not exist for this one. The cold gate caught
    /// it on 2026-09-15: this shape was FLAGGED while the sync twin was cleared, and in the product
    /// <c>DiagnosticScriptRunner.ProcedureExistsAsync</c> sat on the debt list as false debt despite
    /// receiving the very connection its only caller had applied the preamble to.
    /// </summary>
    internal static async Task BuildsAfterUpstreamApplyAsync(SqlConnection conn)
    {
        using var cmd = new SqlCommand("SELECT 1", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// THE CONTROL AGAINST OVER-REPORTING. A command on the app's OWN store — the connection string
    /// never came from a monitored-server builder. It must NOT appear in the census at all. Wrapping
    /// this in READ UNCOMMITTED would be a defect, not a fix.
    /// </summary>
    internal static SqlCommand LocalStoreSite()
        => new SqlCommand("SELECT 1", new SqlConnection("Server=(localdb)\\MSSQLLocalDB;Database=app"));

    // ── THE TWO DISPATCH EDGES, ADDED 2026-09-16 ─────────────────────────────────────────────
    // Read the matched-set warning on this class before changing any of the four.

    /// <summary>
    /// The source half of the DELEGATE control: obtains the monitored connection string and hands
    /// down a ZERO-ARGUMENT delegate that opens it. Builds no command itself, so it gets no census
    /// row — the same shape as <see cref="HandsDownConnection"/>, and it must NOT be added to the
    /// exhaustive name list in the control test.
    ///
    /// <para>⚠ <b>ZERO-ARGUMENT is the load-bearing detail.</b>
    /// <c>Data/Services/ServerConfigScriptService.cs:94</c> has a
    /// <c>Func&lt;string, DbConnection&gt;</c> hook of exactly this kind and its methods were ALREADY
    /// on the frozen debt list, because the tainted STRING argument was what the pre-2026-09-16 rule
    /// saw. With no argument there was nothing for that rule to see, and the taint had to come out of
    /// the lambda's RETURN — across <c>ldftn</c>, which was not an edge.</para>
    /// </summary>
    internal static SqlCommand HandsDownADelegate(ServerConnection connection, string serverName)
    {
        var connStr = connection.GetConnectionString(serverName, "master");
        return BuildsOnConnectionFromADelegate(() => new SqlConnection(connStr));
    }

    /// <summary>
    /// Known-bad, REACHED THROUGH A DELEGATE: must be FLAGGED. It can only be flagged if <c>ldftn</c>
    /// records a call edge AND pushes the target's own return-taint, which is the shape every one of
    /// <c>AgentMailChainProbe</c>'s six connection sites has.
    /// </summary>
    internal static SqlCommand BuildsOnConnectionFromADelegate(Func<SqlConnection> open)
        => new SqlCommand("SELECT 1", open());

    /// <summary>
    /// The source half of the delegate FLOOD control: the identical hand-down over a LITERAL local
    /// connection string that never came from a monitored-server builder.
    /// </summary>
    internal static SqlCommand HandsDownALocalDelegate()
        => BuildsOnConnectionFromALocalDelegate(
               () => new SqlConnection("Server=(localdb)\\MSSQLLocalDB;Database=app"));

    /// <summary>
    /// Known-good by ORIGIN: the same delegate shape as <see cref="BuildsOnConnectionFromADelegate"/>
    /// on the app's OWN store. Must NOT appear in the census at all. Do not give it a monitored
    /// connection string — it is half of what proves the delegate edge taints by origin.
    /// </summary>
    internal static SqlCommand BuildsOnConnectionFromALocalDelegate(Func<SqlConnection> open)
        => new SqlCommand("SELECT 1", open());

    /// <summary>
    /// Known-bad, REACHED THROUGH AN INTERFACE: must be FLAGGED, and <c>Direct</c> must be false. An
    /// interface member has no IL, so this is flagged only if the analyser consults the
    /// IMPLEMENTATION's return-taint. This is <c>SessionDataService</c>'s,
    /// <c>BenchmarkService</c>'s and <c>LiveRiskAssessmentSource</c>'s shape.
    /// </summary>
    internal static SqlCommand BuildsOnConnectionFromAnInterface(IControlMonitoredSource source)
        => new SqlCommand("SELECT 1", source.Open());

    /// <summary>
    /// Known-good by ORIGIN, and THE FLOOD CONTROL FOR THE INTERFACE EDGE: the same interface shape
    /// whose only implementation opens a literal LOCAL connection string. Must NOT appear in the
    /// census. If it does, the analyser is tainting because a call went through an interface rather
    /// than because an implementation is monitored, and the app's own store is back in the debt list.
    /// </summary>
    internal static SqlCommand BuildsOnConnectionFromALocalInterface(IControlLocalSource source)
        => new SqlCommand("SELECT 1", source.Open());
}

/// <summary>
/// The MONITORED half of the interface-dispatch control. TOP-LEVEL, and single-implementation — see
/// the remarks on <see cref="SessionSafetyControls"/> for why both of those are load-bearing rather
/// than stylistic. Never instantiated at runtime; it exists so the assembly's metadata carries a real
/// interface map entry for the analyser to read.
/// </summary>
internal interface IControlMonitoredSource
{
    SqlConnection Open();
}

/// <summary>
/// Its only implementation. <see cref="Open"/> hands back a connection built from
/// <see cref="ServerConnection.GetConnectionString(string, string)"/>, a named taint source, so its
/// <c>ReturnsTainted</c> is what has to cross the dispatch boundary.
/// </summary>
internal sealed class ControlMonitoredSource : IControlMonitoredSource
{
    private readonly ServerConnection _connection;

    internal ControlMonitoredSource(ServerConnection connection) => _connection = connection;

    public SqlConnection Open()
        => new SqlConnection(_connection.GetConnectionString("control-specimen", "master"));
}

/// <summary>
/// The LOCAL half of the interface-dispatch control, and a DIFFERENT interface from
/// <see cref="IControlMonitoredSource"/> on purpose: one interface with two implementations would be
/// correctly tainted by the union over its targets, and the negative assertion would prove nothing.
/// </summary>
internal interface IControlLocalSource
{
    SqlConnection Open();
}

/// <summary>Its only implementation: a literal local connection string, never a monitored builder.</summary>
internal sealed class ControlLocalSource : IControlLocalSource
{
    public SqlConnection Open()
        => new SqlConnection("Server=(localdb)\\MSSQLLocalDB;Database=app");
}
