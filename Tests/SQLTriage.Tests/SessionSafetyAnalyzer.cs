/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace SQLTriage.Tests;

/// <summary>
/// Reads a COMPILED ASSEMBLY — metadata and IL — and answers one question about the invariant
/// <b>(S-1) every read this app issues against a client's production server carries the
/// session-safety preamble</b> (<see cref="SQLTriage.Data.SqlSessionSafety"/>): which methods build a
/// SQL command that will run against a MONITORED SERVER, and which of those do not apply the preamble.
///
/// <para><b>WHY THIS DOES NOT READ SOURCE TEXT.</b> S-1 was stated in prose at
/// <c>SqlSessionSafety.cs:11</c> — "every read this app issues" — with nothing enforcing it, and the
/// alert loop ran six recurring background reads against monitored production servers for months
/// without it. A character or regex census cannot close that: this repo has PROVED such censuses fail
/// in BOTH directions on 2026-09-11 (a line-wrapped offender hid from five of them; prose inside a
/// string literal counted as a real instance). So this walks
/// <see cref="MethodBody.GetILAsByteArray"/> and resolves every call and field token through
/// <see cref="Module.ResolveMethod(int, Type[], Type[])"/> /
/// <see cref="Module.ResolveField(int, Type[], Type[])"/>, exactly as
/// <see cref="I1ConfigStoreAnalyzer"/> does. A "call" here is the method the runtime will actually
/// invoke, so formatting, renames, line breaks and extracted helpers cannot fool it, and it needs no
/// new dependency — there is no Roslyn in this test project and this deliberately does not add one.</para>
///
/// <para><b>THE MONITORED-SERVER PREDICATE — WIDENED 2026-09-15, and why it had to be.</b> Until this
/// lane a method was in scope only when it called <see cref="SQLTriage.Data.Models.ServerConnection"/>'s
/// <c>GetConnectionString</c> / <c>GetConnectionStringForDashboard</c> <b>in its own body</b>. The cold
/// gate MEASURED what that reached: <b>49 of 291</b> command-building methods, <b>17%</b>. The other
/// 242 methods / 310 sites / 55 types were invisible for one structural reason — the connection was
/// obtained in a DIFFERENT method and handed down. Two proved examples:
/// <c>AccessSurfaceCollector</c> opens a monitored connection, passes the <c>SqlConnection</c> to its
/// own helpers and builds six commands with no preamble (zero entries); and
/// <c>XEventService</c> issues CREATE/ALTER/DROP EVENT SESSION against a client's production server
/// while containing <b>zero</b> <c>GetConnectionString</c> calls, because <c>Pages/Alerts.razor</c>
/// hands it the string.
///
/// So the predicate is now a TAINT REACHABILITY question, not a same-method question. A value is
/// <i>monitored</i> when it derives from one of those two builders; taint flows through locals, fields,
/// arguments and return values, and across method boundaries to a fixpoint. A method is in scope when a
/// monitored value reaches it AND it sets a command's text.</para>
///
/// <para>⚠ <b>WHAT KEEPS THE APP'S OWN LOCAL STORE OUT IS THE CALL GRAPH, NOT THIS ANALYSER.</b>
/// Stated carefully, because the obvious reading is wrong and the consequence is severe. Roslyn binds a
/// command-text set to the least-derived virtual declaration, so a SQLite write and a SQL Server write
/// emit the IDENTICAL token: measured across this assembly, 272 of the 369 command-text sets are
/// <c>System.Data.Common.DbCommand.set_CommandText</c> and there is not one
/// <c>SqliteCommand.set_CommandText</c> anywhere. <b>The member key cannot tell them apart. Only taint
/// can.</b> The cold gate PROVED the consequence on 2026-09-15 by adding ONE realistic line to
/// <c>SqliteCipherHelper</c> that passed a monitored connection string into the local-store opener: the
/// census went 96 methods -> <b>202</b>, flooding with the app's own caches and history tables. The
/// ratchet fires loudly when that happens, so it is not silent - but its failure message says a
/// client's production server is involved, so READ THE CONNECTION before acting on any new entry.</para>
///
/// <para><b>GUARDEDNESS TRAVELS THE SAME EDGES.</b> <c>ApplyAsync</c> configures the SESSION, not one
/// command: proved live against <c>.\new2022</c> by the cold gate on 2026-09-15 — a later, separate
/// batch on the same connection reported <c>lock_timeout = 5000</c>, <c>isolation = 1</c>. So one
/// <c>ApplyAsync</c> covers every command built on that connection in that method, AND in the methods
/// it hands the connection down to. Without this, widening the census would have flooded the debt list
/// with code that is already correct — and the previous, narrower census already put one correct
/// service (<c>DiagnosticScriptRunner.ExecuteScriptAsync</c>) on the debt list for exactly this reason.
/// The prefix forms (<c>DefaultPrefix</c> / <c>BuildPrefix</c>) are per-batch, so those are counted
/// per site and are NOT propagated.</para>
///
/// <para><b>EVERY BODY THE CLR COULD DISPATCH TO — ADDED 2026-09-16, and why a metadata token
/// was not enough.</b> Until this lane a callee was resolved ONLY by
/// <c>(Module, MetadataToken)</c> — the body the METADATA NAMES, which is not always the body that
/// RUNS. Two structural consequences, both observed as ABSENCES from the census at <c>97f158f</c>.
/// (1) An interface member has no IL, so it is not in the graph at all and its implementation's
/// <c>ReturnsTainted</c> was never consulted: a connection obtained through
/// <c>IDbConnectionFactory.CreateConnection()</c> lost its taint at the dispatch boundary.
/// <c>Data/SessionDataService.cs</c> was the proof inside ONE file — <c>FetchSessionsAsync</c>
/// casts to the concrete factory at <c>:144</c> and WAS counted, while
/// <c>GetBlockingChainAsync</c>, <c>GetQueryPlanAsync</c> and <c>KillSessionAsync</c> take the
/// interface at <c>:200</c>, <c>:302</c> and <c>:350</c> and were ABSENT. One of four visible, and
/// the invisible one runs <c>KILL {spid}</c>. (2) <c>ldftn</c> was not an edge at all, so a
/// connection opened inside a lambda and handed out as a <c>Func&lt;DbConnection&gt;</c> never
/// reached the method that invoked the delegate — <c>Data/Services/AgentMailChainProbe.cs</c>
/// builds every one of its six connections that way and had no row.
///
/// So the rule, stated as a rule because a type name would ship a SET as an INSTANCE: <b>when this
/// analyser reads a callee's return-taint it consults every body the CLR could actually dispatch
/// to</b> — <c>Graph.DispatchTargets</c>, built from <c>Type.GetInterfaceMap</c> and
/// <c>MethodInfo.GetBaseDefinition</c>, which ARE the CLR's own interface dispatch table and its
/// own override links, plus the target of an <c>ldftn</c> / <c>ldvirtftn</c>. Neither
/// <c>IDbConnectionFactory</c> nor <c>Func&lt;DbConnection&gt;</c> is named anywhere in this file;
/// they are today's instances of it.
///
/// <b>⚠ WHY THIS IS NOT THE 2026-09-15 FLOOD AGAIN.</b> That flood came from widening the taint
/// RELATION (any tainted argument taints the result), which tainted <c>reader.GetString(0)</c> and
/// carried a monitored server's DATA into the app's own SQLite store — 96 methods to 202. This
/// change adds no taint SOURCE and no new relation: <see cref="MonitoredServerConnStringBuilders"/>,
/// <see cref="ConnectionCarrying"/>, <see cref="IsLiveConnection"/> and <see cref="IsStringBuilding"/>
/// are byte-for-byte unchanged, so what counts as a live connection and what a command is AIMED at
/// are the same functions of the same six types. It widens the EDGE SET only, to edges the runtime
/// itself takes. What goes RED if either new edge stops working — or if <c>GetInterfaceMap</c>
/// throws for every type and the map silently comes back EMPTY — is the four compiled specimens in
/// <c>SessionSafetyControls</c> whose names begin <c>BuildsOnConnectionFrom</c>, pinned by name in
/// <c>SessionSafetyCensusTests.The_analyser_flags_reached_sites_and_clears_guarded_and_local_ones</c>:
/// two REQUIRED PRESENT (monitored, through an interface and through a delegate) and two REQUIRED
/// ABSENT (the same two shapes over a local-store connection string). The absent pair is the flood
/// control — it proves a dispatch edge taints only when an implementation is genuinely
/// monitored.</para>
///
/// <para><b>⚠ THE BOUNDARY, named so it is not mistaken for a total guarantee.</b>
/// <list type="bullet">
/// <item>The unit of analysis is still the METHOD. A monitored value reaching a method makes ALL of that
/// method's command sites count; this does not prove the preamble was concatenated onto THAT command's
/// text, only that it was referenced beside it (<see cref="MethodCensus.Prefixes"/> vs
/// <see cref="MethodCensus.Sites"/>).</item>
/// <item>⚠ <b>THE WALK OVER-APPROXIMATES IN BOTH DIRECTIONS. A REPORTED SITE IS A LEAD, NOT A
/// VERDICT.</b> An earlier version of this list claimed "the failure mode is a MISSED site, not an
/// invented one". <b>That was FALSE and the cold gate refuted it</b> with a compiled control on
/// 2026-09-15: a method that opens a monitored connection, disposes it, REASSIGNS THE SAME LOCAL to a
/// local-store connection string and builds its only command on that was reported as an unguarded
/// monitored-server site. The walk is linear with no control-flow graph, taint on a local is monotone
/// (<c>locals[i] |= v</c>, never cleared), fields are flow-insensitive and parameters
/// context-insensitive. So <b>before "fixing" any entry, confirm which connection that command is
/// really on</b> — wrapping a local-store write in READ UNCOMMITTED is the defect this census exists to
/// prevent, and a census that invites it has done harm, not good.</item>
/// <item><c>ApplyAsync</c> coverage is judged per METHOD, not per connection object. A method that opens
/// two connections and applies the preamble to one of them reads as covered for both.</item>
/// <item>Taint through arrays is tracked as a single per-method flag, not per element.</item>
/// <item>Propagation stops at the assembly boundary: a connection handed to a method in another
/// assembly and handed back is not followed.</item>
/// <item>⚠ <b>ONLY RETURN-taint travels a dispatch edge; ARGUMENT taint does not.</b> A monitored
/// connection passed INTO an interface member is delivered to the body the token names and not to
/// the implementations. Deliberate, and this is the flood-prone direction: N implementations of
/// which one is wired would produce N-1 rows about code that never runs. Nothing in the 2026-09-16
/// lane needed it — every newly visible row came through the return direction. The same asymmetry
/// applies to GUARDEDNESS, which travels as an argument (<c>ArgGuard</c>): a connection obtained
/// through an interface AND already covered by an upstream <c>ApplyAsync</c> would read as FALSE
/// DEBT. It cannot bite today, because all four newly visible types contain ZERO references to
/// <c>SqlSessionSafety</c> (counted, not assumed), but it is live for the next type. The check is
/// whether a newly visible row has <c>prefixes &gt; 0</c> or belongs to a type that calls
/// <c>ApplyAsync</c> anywhere.</item>
/// <item>⚠ A dispatch edge is recorded only when the DECLARATION is declared in the assembly under
/// analysis. That is a deliberate narrowing, not an oversight: without it every app type's
/// <c>ToString()</c> override maps onto <c>System.Object.ToString</c>, and ONE tainted override
/// would taint the result of every <c>ToString()</c> call in the assembly — a tainted string into
/// <c>new SqliteConnection(str)</c> is exactly the 2026-09-15 flood route. So a product type
/// implementing a FRAMEWORK interface, or overriding a FRAMEWORK virtual, that hands back a
/// connection is still invisible. None exists today; it is a named gap, not a silent one.</item>
/// </list>
/// Every one of these is a visible refactor, not silent drift, and
/// <c>SessionSafetyCensusTests</c> compiles controls for the positive AND the negative direction so a
/// broken walk goes RED rather than green.</para>
/// </summary>
internal static class SessionSafetyAnalyzer
{
    /// <summary>
    /// Builders of a connection string aimed at a monitored server — the ONLY taint sources. These are
    /// the only builders whose <c>DataSource</c> is a monitored server name (<c>ServerConnection.cs:215</c>).
    ///
    /// <para>⚠ <b>THESE ARE STRING LITERALS AND NOTHING COMPILE-CHECKS THEM.</b> This comment used to
    /// claim "a rename that keeps the behaviour breaks the build rather than silently emptying this set".
    /// <b>That was false</b> — the cold gate checked, 2026-09-15: there is not one <c>nameof</c> in this
    /// file, and these are compared against a resolved <c>DeclaringType.FullName + "." + Name</c> at
    /// runtime. Rename <c>ServerConnection.GetConnectionString</c> and this set matches nothing.
    /// <b>What actually catches that</b> is the pair of assertions in <c>SessionSafetyCensusTests</c>:
    /// the census would collapse toward zero and both <c>NotBeEmpty</c> and the profile-measured
    /// <c>MeasuredCensusFloor</c> go RED. So the outcome is safe; the stated mechanism was not, and a
    /// reader who believed it would have skipped the floor when trimming this test.</para>
    /// </summary>
    internal static readonly HashSet<string> MonitoredServerConnStringBuilders = new(StringComparer.Ordinal)
    {
        "SQLTriage.Data.Models.ServerConnection.GetConnectionString",
        "SQLTriage.Data.Models.ServerConnection.GetConnectionStringForDashboard",
    };

    /// <summary>
    /// Ways a method sets the text a SQL command will execute — the point at which the preamble either
    /// is or is not present. Construction covers <c>new SqlCommand(text, conn)</c>; the property setter
    /// covers <c>cmd.CommandText = text</c> after a <c>CreateCommand()</c>.
    /// </summary>
    internal static readonly HashSet<string> CommandTextSites = new(StringComparer.Ordinal)
    {
        "Microsoft.Data.SqlClient.SqlCommand..ctor",
        "Microsoft.Data.SqlClient.SqlCommand.set_CommandText",
        "System.Data.Common.DbCommand.set_CommandText",
        "System.Data.IDbCommand.set_CommandText",
    };

    /// <summary>The per-batch forms of the preamble. Counted per site; never propagated to a callee.</summary>
    internal static readonly HashSet<string> PrefixMembers = new(StringComparer.Ordinal)
    {
        "SQLTriage.Data.SqlSessionSafety.DefaultPrefix",
        "SQLTriage.Data.SqlSessionSafety.BuildPrefix",
    };

    /// <summary>The session form. Covers every later command on that connection — see the class remarks.</summary>
    internal const string SessionApplyMember = "SQLTriage.Data.SqlSessionSafety.ApplyAsync";

    /// <summary>
    /// The guard's own definition. Excluded from the census because <c>ApplyAsync</c> necessarily sets a
    /// command's text on the caller's monitored connection — reporting the guard as a site it guards
    /// would be noise, and a debt list that contains the guard teaches the next reader nothing.
    /// </summary>
    internal const string GuardTypeFullName = "SQLTriage.Data.SqlSessionSafety";

    /// <summary>
    /// Types that can CARRY a monitored connection, and therefore the only call results taint flows into.
    ///
    /// <para><b>⚠ THIS DISTINCTION IS THE DIFFERENCE BETWEEN A CENSUS AND A FLOOD, and it was MEASURED,
    /// not guessed.</b> The first widened build propagated taint to any call result with a tainted
    /// argument. That rule tainted DATA READ OUT of a monitored connection — <c>reader.GetString(0)</c>
    /// on a reader from a monitored command — and those strings were then passed to services that write
    /// to the app's OWN encrypted SQLite store. <c>BlockingHistoryService.RecordDeadlockAsync</c> came
    /// back as an unguarded monitored-server site when every one of its commands runs against
    /// <c>SqliteCipherHelper.OpenEncrypted(_connectionString)</c>, a local file. Freezing that would have
    /// recorded false debt, and the next reader "fixing" it would have wrapped a LOCAL WRITE in READ
    /// UNCOMMITTED — the exact defect this census exists to prevent.</para>
    ///
    /// <para>So: a row value read from a monitored server is not a connection to it. Taint reaches a call
    /// result only when that result can hold the connection itself.</para>
    /// </summary>
    private static readonly Type[] ConnectionCarrying =
    {
        typeof(System.Data.Common.DbConnection),
        typeof(System.Data.Common.DbCommand),
        typeof(System.Data.Common.DbTransaction),
        typeof(System.Data.IDbConnection),
        typeof(System.Data.IDbCommand),
        typeof(System.Data.IDbTransaction),
    };

    /// <summary>
    /// One method's tally. <see cref="Sites"/> = command texts set in this method ON A CONNECTION A
    /// MONITORED-SERVER CONNECTION STRING REACHES — commands this method builds against the app's own
    /// local store are not counted and must not be, since READ UNCOMMITTED on those would be a defect;
    /// <see cref="Prefixes"/> = per-batch preamble references in the same method;
    /// <see cref="SessionApplied"/> = this method (or a caller that handed it the connection) ran
    /// <c>ApplyAsync</c>, which covers the whole session; <see cref="Direct"/> = this method called a
    /// connection-string builder itself, i.e. it was already visible to the pre-2026-09-15 census.
    /// </summary>
    internal sealed record MethodCensus(
        string Owner, string Method, int Sites, int Prefixes, bool SessionApplied, bool Direct)
    {
        internal bool Unguarded => Sites > 0 && !SessionApplied && Prefixes < Sites;

        /// <summary>
        /// The freeze key. ⚠ It carries the SITE AND PREFIX COUNTS deliberately. Freezing by method NAME
        /// alone let a frozen method accumulate unlimited new unguarded production reads in silence — the
        /// cold gate PROVED it on 2026-09-15 by adding a second unguarded command build inside
        /// <c>WaitStatsService.GetSnapshotAsync</c>: the census went sites=1 → sites=2 and all three
        /// tests stayed GREEN. With the counts in the key, that edit changes the key, the key is not in
        /// the frozen set, and the ratchet fires.
        /// </summary>
        internal string Key => $"{Owner}.{Method} sites={Sites} prefixes={Prefixes}";

        public override string ToString() =>
            Key + (SessionApplied ? " apply" : "") + (Direct ? " direct" : " reached")
                + (Unguarded ? "  <== UNGUARDED" : "");
    }

    /// <summary>
    /// Every method in <paramref name="asm"/> that builds a SQL command reachable by a monitored-server
    /// connection, with its guard counts. Returns the WHOLE distribution — callers assert against it
    /// rather than filtering first, because a filter-only census reports zero when the instrument itself
    /// is broken and zero is the greenest possible result.
    ///
    /// <para><paramref name="typeFilter"/> narrows WHAT IS REPORTED only. The call graph is always built
    /// over the whole assembly: filtering the graph would sever the very edges this analysis exists to
    /// follow, and a narrowed census would silently answer a different question.</para>
    /// </summary>
    internal static IReadOnlyList<MethodCensus> Census(Assembly asm, Func<Type, bool>? typeFilter = null)
    {
        var graph = Build(asm);
        Propagate(graph);

        var result = new List<MethodCensus>();
        foreach (var r in graph.Methods.Values)
        {
            if (r.Sites == 0) continue;
            if (!r.Monitored) continue;

            var owner = Root(r.Method.DeclaringType!);
            if (owner.FullName == GuardTypeFullName) continue;
            if (typeFilter != null && !typeFilter(owner)) continue;

            result.Add(new MethodCensus(
                owner.FullName ?? owner.Name, OriginName(r.Method),
                r.Sites, r.Prefixes, r.SessionApplied, r.DirectSource));
        }

        // Fold the compiler's split of one authored method (async state machine + its lambdas) back
        // into a single row, so the census reports what a developer can actually go and edit.
        return result
            .GroupBy(x => (x.Owner, x.Method))
            .Select(g => new MethodCensus(
                g.Key.Owner, g.Key.Method,
                g.Sum(x => x.Sites), g.Sum(x => x.Prefixes),
                g.Any(x => x.SessionApplied), g.Any(x => x.Direct)))
            .OrderBy(r => r.Owner, StringComparer.Ordinal)
            .ThenBy(r => r.Method, StringComparer.Ordinal)
            .ToList();
    }

    // ── The graph ────────────────────────────────────────────────────────────────────────────────

    private sealed class MethodRec
    {
        public MethodBase Method = null!;
        public byte[] Il = Array.Empty<byte>();
        public int ArgCount;
        public bool[] ArgTaint = Array.Empty<bool>();
        public bool[] ArgGuard = Array.Empty<bool>();
        public int Sites;
        public int Prefixes;
        public bool AppliesHere;      // calls ApplyAsync in its own body
        public bool DirectSource;     // calls a connection-string builder in its own body
        public bool Monitored;
        public bool ReturnsTainted;   // hands a monitored connection back to its callers
        public bool GuardedByUpstream;
        public bool Queued;
        public readonly List<(Module Mod, int Token)> FieldsRead = new();

        public bool SessionApplied => AppliesHere || ArgGuard.Any(g => g) || GuardedByUpstream;
    }

    private sealed class Graph
    {
        public readonly Dictionary<(Module, int), MethodRec> Methods = new();
        public readonly HashSet<(Module, int)> TaintedFields = new();
        public readonly HashSet<(Module, int)> GuardedFields = new();
        public readonly Dictionary<(Module, int), List<MethodRec>> FieldReaders = new();
        public readonly Dictionary<(Module, int), List<MethodRec>> Callers = new();
        /// <summary>
        /// Every body the CLR could dispatch to, per DECLARATION token: an interface member's
        /// implementations and a virtual method's overrides. Built in <see cref="Build"/> from
        /// metadata STRUCTURE — the CLR's own interface map and its own override links — never from a
        /// name or a signature match, because a name match is not a dispatch edge and would also
        /// collapse unrelated same-named members across types. Empty for every declaration nothing
        /// dispatches through, which is nearly all of them.
        /// </summary>
        public readonly Dictionary<(Module, int), List<MethodRec>> DispatchTargets = new();
        /// <summary>Authored methods by declaring type and name, so an async state machine's
        /// <c>SetResult</c> can be attributed back to the method a developer actually wrote.</summary>
        public readonly Dictionary<(Type, string), List<MethodRec>> ByName = new();
        public readonly Queue<MethodRec> Work = new();

        public void Enqueue(MethodRec r)
        {
            if (r.Queued) return;
            r.Queued = true;
            Work.Enqueue(r);
        }
    }

    /// <summary>
    /// Pass one: decode every method once, take the tallies that do not depend on taint (sites, prefix
    /// references, ApplyAsync, direct sources) and index which methods read which fields.
    /// </summary>
    private static Graph Build(Assembly asm)
    {
        var g = new Graph();

        foreach (var t in TypesOf(asm))
        {
            if (t == null) continue;
            foreach (var m in DeclaredMethods(t))
            {
                var il = IlOf(m);
                if (il == null) continue;

                var r = new MethodRec
                {
                    Method = m,
                    Il = il,
                    ArgCount = m.GetParameters().Length + (m.IsStatic ? 0 : 1),
                };
                r.ArgTaint = new bool[Math.Max(1, r.ArgCount)];
                r.ArgGuard = new bool[Math.Max(1, r.ArgCount)];

                var mod = m.Module;
                foreach (var ins in Decode(il))
                {
                    if (ins.Op == OpCodes.Ldsfld || ins.Op == OpCodes.Ldsflda
                     || ins.Op == OpCodes.Ldfld || ins.Op == OpCodes.Ldflda)
                    {
                        var f = ResolveField(mod, ins.Token, m);
                        if (f == null) continue;
                        if (PrefixMembers.Contains(KeyOf(f.DeclaringType, f.Name))) r.Prefixes++;
                        var id = (f.Module, f.MetadataToken);
                        r.FieldsRead.Add(id);
                        if (!g.FieldReaders.TryGetValue(id, out var list))
                            g.FieldReaders[id] = list = new List<MethodRec>();
                        list.Add(r);
                        continue;
                    }
                    // Taking a method's ADDRESS for a delegate is a call edge too: that body WILL
                    // run, just not here. Without this edge SetReturnsTainted can never wake the
                    // method that constructed the delegate, and a lambda that opens a monitored
                    // connection leaves every command site downstream of `open()` invisible.
                    // ⚠ It must NOT fall through to the key classification below. Taking an address is
                    // not calling it, and letting `ldftn` reach that code would set DirectSource on a
                    // method that merely REFERENCES a connection-string builder.
                    if (ins.Op == OpCodes.Ldftn || ins.Op == OpCodes.Ldvirtftn)
                    {
                        var target = Resolve(mod, ins.Token, m);
                        if (target == null) continue;
                        var tid = (target.Module, target.MetadataToken);
                        if (!g.Callers.TryGetValue(tid, out var dl))
                            g.Callers[tid] = dl = new List<MethodRec>();
                        dl.Add(r);
                        continue;
                    }
                    if (ins.Op != OpCodes.Call && ins.Op != OpCodes.Callvirt && ins.Op != OpCodes.Newobj)
                        continue;

                    var called = Resolve(mod, ins.Token, m);
                    if (called == null) continue;
                    var key = KeyOf(called.DeclaringType, called.Name);

                    if (MonitoredServerConnStringBuilders.Contains(key)) r.DirectSource = true;
                    else if (key == SessionApplyMember) r.AppliesHere = true;
                    else if (PrefixMembers.Contains(key)) r.Prefixes++;

                    var callee = (called.Module, called.MetadataToken);
                    if (!g.Callers.TryGetValue(callee, out var cl))
                        g.Callers[callee] = cl = new List<MethodRec>();
                    cl.Add(r);
                }

                g.Methods[(mod, m.MetadataToken)] = r;

                var declaring = m.DeclaringType;
                if (declaring != null)
                {
                    var nameKey = (declaring, m.Name);
                    if (!g.ByName.TryGetValue(nameKey, out var byName))
                        g.ByName[nameKey] = byName = new List<MethodRec>();
                    byName.Add(r);
                }
            }
        }

        BuildDispatchTargets(g, asm);

        foreach (var r in g.Methods.Values) g.Enqueue(r);
        return g;
    }

    /// <summary>
    /// Pass one and a half: the dispatch map, from METADATA STRUCTURE only.
    /// <c>Type.GetInterfaceMap</c> IS the CLR's interface dispatch table and
    /// <c>MethodInfo.GetBaseDefinition</c> IS its override link, so this reads the runtime's own answer
    /// to "which body runs" rather than guessing one from names.
    ///
    /// <para>⚠ <b>THE <c>asm</c> FILTER IS LOAD-BEARING — see the third boundary item on this class.</b>
    /// Only a declaration declared in the assembly under analysis gets an edge. Every app type's
    /// <c>ToString()</c> override otherwise lands on <c>System.Object.ToString</c>, and one tainted
    /// override there would taint the result of every <c>ToString()</c> call in the assembly.</para>
    ///
    /// <para>⚠ <b>THE CATCHES SWALLOW SILENTLY, WHICH IS THE SHAPE THIS CENSUS EXISTS TO DISTRUST.</b>
    /// <c>GetInterfaceMap</c> throws on generic type parameters and on partially loaded types, and
    /// <c>DeclaredMethods</c> / <c>IlOf</c> / <c>Resolve</c> already swallow the same way. A swallowed
    /// type drops its edges and the census gets SMALLER, which reads as progress. What makes that loud
    /// rather than silent is not this method: it is the four compiled specimens named on the class
    /// remarks, which turn RED the moment this map comes back empty. If you need the swallow COUNT,
    /// instrument this method — it is not reported today.</para>
    /// </summary>
    private static void BuildDispatchTargets(Graph g, Assembly asm)
    {
        foreach (var t in TypesOf(asm))
        {
            if (t == null || t.IsInterface) continue;

            Type[] itfs;
            try { itfs = t.GetInterfaces(); } catch { itfs = Array.Empty<Type>(); }

            foreach (var itf in itfs)
            {
                if (itf.Assembly != asm) continue;
                try
                {
                    var map = t.GetInterfaceMap(itf);
                    int pairs = Math.Min(map.InterfaceMethods.Length, map.TargetMethods.Length);
                    for (int i = 0; i < pairs; i++)
                    {
                        var decl = map.InterfaceMethods[i];
                        var impl = map.TargetMethods[i];
                        if (decl == null || impl == null) continue;
                        if (!g.Methods.TryGetValue((impl.Module, impl.MetadataToken), out var rec)) continue;
                        AddDispatchTarget(g, (decl.Module, decl.MetadataToken), rec);
                    }
                }
                catch { }
            }

            foreach (var m in DeclaredMethods(t))
            {
                if (m is not MethodInfo mi || !mi.IsVirtual || mi.IsAbstract) continue;
                MethodInfo? baseDef;
                try { baseDef = mi.GetBaseDefinition(); } catch { continue; }
                if (baseDef == null) continue;
                if (baseDef.Module == mi.Module && baseDef.MetadataToken == mi.MetadataToken) continue;
                if (baseDef.DeclaringType?.Assembly != asm) continue;
                if (!g.Methods.TryGetValue((mi.Module, mi.MetadataToken), out var over)) continue;
                AddDispatchTarget(g, (baseDef.Module, baseDef.MetadataToken), over);
            }
        }

        // The DECLARATION's callers become each implementation's callers, so an implementation that
        // later turns ReturnsTainted wakes the methods that reached it THROUGH the declaration.
        // Without this the fixpoint would depend on the order the worklist happened to walk in.
        foreach (var kv in g.DispatchTargets)
        {
            if (!g.Callers.TryGetValue(kv.Key, out var declCallers)) continue;
            foreach (var impl in kv.Value)
            {
                var implKey = (impl.Method.Module, impl.Method.MetadataToken);
                if (!g.Callers.TryGetValue(implKey, out var cl))
                    g.Callers[implKey] = cl = new List<MethodRec>();
                cl.AddRange(declCallers);
            }
        }
    }

    private static void AddDispatchTarget(Graph g, (Module, int) declaration, MethodRec impl)
    {
        if (!g.DispatchTargets.TryGetValue(declaration, out var impls))
            g.DispatchTargets[declaration] = impls = new List<MethodRec>();
        if (!impls.Contains(impl)) impls.Add(impl);
    }

    /// <summary>
    /// Pass two: run the taint dataflow to a fixpoint over a worklist. A method is re-analysed only when
    /// one of its parameters or a field it reads becomes tainted, so the fixpoint costs a handful of
    /// re-walks rather than a full sweep per round.
    /// </summary>
    private static void Propagate(Graph g)
    {
        // A backstop, not a design parameter: the lattice is two booleans per parameter and per field,
        // so this terminates on its own well inside the bound. It THROWS rather than returning what it
        // has, because a truncated fixpoint is an under-reporting census, and an under-reporting census
        // is the greenest possible result — precisely the silent-false-clean shape this guard exists to
        // prevent. A loud failure here is a bug report; a quiet one is a false assurance.
        long budget = (long)g.Methods.Count * 40 + 200_000;

        while (g.Work.Count > 0)
        {
            if (budget-- <= 0)
                throw new InvalidOperationException(
                    "SessionSafetyAnalyzer taint fixpoint did not converge within "
                    + ((long)g.Methods.Count * 40 + 200_000) + " steps over " + g.Methods.Count
                    + " methods. The census would be INCOMPLETE, so it fails loudly instead of "
                    + "reporting a partial result as a clean one.");

            var r = g.Work.Dequeue();
            r.Queued = false;
            Walk(r, g);
        }
    }

    private static void Walk(MethodRec r, Graph g)
    {
        var m = r.Method;
        var mod = m.Module;
        var stack = new List<bool>(16);
        var locals = new List<bool>(16);
        bool arrayTaint = false;
        bool monitored = r.DirectSource;
        bool guardedOut = r.SessionApplied;
        int sites = 0;

        bool Local(int i)
        {
            while (locals.Count <= i) locals.Add(false);
            return locals[i];
        }
        void SetLocal(int i, bool v)
        {
            while (locals.Count <= i) locals.Add(false);
            locals[i] |= v;                      // monotone: a local never becomes clean again
        }
        void Push(bool v) { stack.Add(v); if (v) monitored = true; }
        bool Pop()
        {
            if (stack.Count == 0) return false;  // branch-target desync: resynchronise, do not invent
            var v = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return v;
        }

        bool Arg(int i) => i < r.ArgTaint.Length && r.ArgTaint[i];

        foreach (var ins in Decode(r.Il))
        {
            var op = ins.Op;

            // ── locals and arguments ──
            if (op == OpCodes.Ldarg_0) { Push(Arg(0)); continue; }
            if (op == OpCodes.Ldarg_1) { Push(Arg(1)); continue; }
            if (op == OpCodes.Ldarg_2) { Push(Arg(2)); continue; }
            if (op == OpCodes.Ldarg_3) { Push(Arg(3)); continue; }
            if (op == OpCodes.Ldarg || op == OpCodes.Ldarg_S
             || op == OpCodes.Ldarga || op == OpCodes.Ldarga_S) { Push(Arg(ins.Operand)); continue; }
            if (op == OpCodes.Starg || op == OpCodes.Starg_S)
            {
                var v = Pop();
                if (v && ins.Operand < r.ArgTaint.Length) r.ArgTaint[ins.Operand] = true;
                continue;
            }
            if (op == OpCodes.Ldloc_0) { Push(Local(0)); continue; }
            if (op == OpCodes.Ldloc_1) { Push(Local(1)); continue; }
            if (op == OpCodes.Ldloc_2) { Push(Local(2)); continue; }
            if (op == OpCodes.Ldloc_3) { Push(Local(3)); continue; }
            if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S
             || op == OpCodes.Ldloca || op == OpCodes.Ldloca_S) { Push(Local(ins.Operand)); continue; }
            if (op == OpCodes.Stloc_0) { SetLocal(0, Pop()); continue; }
            if (op == OpCodes.Stloc_1) { SetLocal(1, Pop()); continue; }
            if (op == OpCodes.Stloc_2) { SetLocal(2, Pop()); continue; }
            if (op == OpCodes.Stloc_3) { SetLocal(3, Pop()); continue; }
            if (op == OpCodes.Stloc || op == OpCodes.Stloc_S) { SetLocal(ins.Operand, Pop()); continue; }
            if (op == OpCodes.Dup) { var v = Pop(); Push(v); Push(v); continue; }

            // ── fields. The async state machine hoists every local into a field, so field taint is
            //    what carries a connection across an await; it is tracked assembly-wide and
            //    flow-insensitively, which is the conservative direction. ──
            if (op == OpCodes.Ldsfld || op == OpCodes.Ldsflda)
            {
                var f = ResolveField(mod, ins.Token, m);
                Push(f != null && g.TaintedFields.Contains((f.Module, f.MetadataToken)));
                if (f != null && g.GuardedFields.Contains((f.Module, f.MetadataToken))) guardedOut = true;
                continue;
            }
            if (op == OpCodes.Ldfld || op == OpCodes.Ldflda)
            {
                bool obj = Pop();                        // the object reference
                var f = ResolveField(mod, ins.Token, m);
                // A field read off a MONITORED OBJECT carries the connection too — that is how
                // `(IDbConnection, bool)` from a rent helper gives up its Item1, and tuple element
                // access is a field read, not a call.
                Push(f != null
                     && (g.TaintedFields.Contains((f.Module, f.MetadataToken))
                         || (obj && IsConnectionCarrying(f.FieldType))));
                if (f != null && g.GuardedFields.Contains((f.Module, f.MetadataToken))) guardedOut = true;
                continue;
            }
            if (op == OpCodes.Stsfld || op == OpCodes.Stfld)
            {
                var v = Pop();
                if (op == OpCodes.Stfld) Pop();          // the object reference
                if (!v) continue;
                var f = ResolveField(mod, ins.Token, m);
                if (f == null) continue;
                var id = (f.Module, f.MetadataToken);
                if (g.TaintedFields.Add(id) && g.FieldReaders.TryGetValue(id, out var readers))
                    foreach (var reader in readers) g.Enqueue(reader);
                if (guardedOut && g.GuardedFields.Add(id) && g.FieldReaders.TryGetValue(id, out var gr))
                    foreach (var reader in gr) g.Enqueue(reader);
                continue;
            }

            // ── arrays, tracked as one flag for the whole method ──
            if (op == OpCodes.Stelem || op == OpCodes.Stelem_Ref || op == OpCodes.Stelem_I
             || op == OpCodes.Stelem_I1 || op == OpCodes.Stelem_I2 || op == OpCodes.Stelem_I4
             || op == OpCodes.Stelem_I8 || op == OpCodes.Stelem_R4 || op == OpCodes.Stelem_R8)
            {
                var v = Pop(); Pop(); Pop();
                arrayTaint |= v;
                continue;
            }
            if (op == OpCodes.Ldelem || op == OpCodes.Ldelem_Ref || op == OpCodes.Ldelem_I
             || op == OpCodes.Ldelem_I1 || op == OpCodes.Ldelem_I2 || op == OpCodes.Ldelem_I4
             || op == OpCodes.Ldelem_I8 || op == OpCodes.Ldelem_U1 || op == OpCodes.Ldelem_U2
             || op == OpCodes.Ldelem_U4 || op == OpCodes.Ldelem_R4 || op == OpCodes.Ldelem_R8
             || op == OpCodes.Ldelema)
            {
                Pop(); Pop();
                Push(arrayTaint);
                continue;
            }

            // ── calls ──
            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
            {
                var called = Resolve(mod, ins.Token, m);
                if (called == null) { stack.Clear(); continue; }

                int n = called.GetParameters().Length
                      + ((op != OpCodes.Newobj && !called.IsStatic) ? 1 : 0);

                var argv = new bool[n];
                for (int i = n - 1; i >= 0; i--) argv[i] = Pop();

                var key = KeyOf(called.DeclaringType, called.Name);

                if (MonitoredServerConnStringBuilders.Contains(key))
                {
                    monitored = true;
                    Push(true);
                    continue;
                }

                bool any = argv.Any(a => a);
                g.Methods.TryGetValue((called.Module, called.MetadataToken), out var callee);

                // ── IS THIS COMMAND AIMED AT A MONITORED SERVER? ──
                // Asked of the COMMAND, not of the method. "A monitored value exists somewhere in this
                // method" is not the same question, and the difference is measurable: it reported
                // ChangeItemService.LogChange and ReloadCache as unguarded monitored-server reads when
                // every command in that class runs against SqliteCipherHelper.OpenEncrypted(...) — a
                // local encrypted file. A caller merely handed it a string that came from a monitored
                // connection. So the test is whether a CONNECTION or COMMAND argument is monitored:
                // `new SqlCommand(sql, conn)` asks about conn; `cmd.CommandText = sql` asks about cmd,
                // which is monitored exactly when it came off a monitored connection.
                if (CommandTextSites.Contains(key))
                {
                    var ps = called.GetParameters();
                    bool instance = op != OpCodes.Newobj && !called.IsStatic;
                    for (int i = 0; i < n; i++)
                    {
                        if (!argv[i]) continue;
                        Type? pt = instance
                            ? (i == 0 ? called.DeclaringType : (i - 1 < ps.Length ? ps[i - 1].ParameterType : null))
                            : (i < ps.Length ? ps[i].ParameterType : null);
                        if (!IsLiveConnection(pt)) continue;
                        sites++;
                        break;
                    }
                }

                if (any)
                {
                    monitored = true;

                    // The connection travelling into another method — the edge this whole lane exists
                    // to follow — and, alongside it, the knowledge that its session is already configured.
                    if (key != SessionApplyMember && callee != null)
                    {
                        for (int i = 0; i < n && i < callee.ArgTaint.Length; i++)
                        {
                            if (!argv[i]) continue;
                            if (!callee.ArgTaint[i]) { callee.ArgTaint[i] = true; g.Enqueue(callee); }
                            if (guardedOut && !callee.ArgGuard[i]) { callee.ArgGuard[i] = true; g.Enqueue(callee); }
                        }
                    }

                    // An async method returns its value through the builder, not through `ret`, so this
                    // is where a `private async Task<string> ...GetConnStrAsync()` hands taint back.
                    if (called.Name == "SetResult"
                        && called.DeclaringType?.FullName?.StartsWith(
                               "System.Runtime.CompilerServices.Async", StringComparison.Ordinal) == true)
                        MarkOriginReturnsTainted(r, g);
                }

                bool returnsValue = op == OpCodes.Newobj
                    || (called is MethodInfo mi && mi.ReturnType != typeof(void));
                if (!returnsValue) continue;

                // WHAT THE RESULT CARRIES. Not "anything touched by a monitored value" — see the remarks
                // on ConnectionCarrying for the measured reason that rule floods the census with local
                // SQLite writes. A result is monitored when the callee hands one back, when a monitored
                // value is being poured into something that can hold a connection, or when a connection
                // string is being assembled.
                var resultType = op == OpCodes.Newobj ? called.DeclaringType : (called as MethodInfo)?.ReturnType;
                // ⚠ THE DISPATCH DISJUNCT IS UNCONDITIONAL, and that is a decision. It sits beside
                // the direct-callee branch rather than behind `callee == null`, and it carries no
                // return-type gate, because the direct branch has neither: gating it would make a
                // method's visibility depend on its DISPATCH FORM, which is the exact defect
                // 2026-09-16 closed. (A return-type gate would also be nearly cosmetic here —
                // IsConnectionCarrying accepts `string`.)
                bool resultTainted =
                       (callee != null && callee.ReturnsTainted)
                    || DispatchReturnsTainted(g, called)
                    || (any && (IsConnectionCarrying(resultType) || IsStringBuilding(called)));

                Push(resultTainted);
                continue;
            }

            // ── taint pass-throughs. A cast is not a laundering operation: `(SqlConnection)o` and
            //    `o as SqlConnection` carry the monitored connection across unchanged, and dropping the
            //    taint here would lose every site behind a cast or a boxed hand-off. ──
            if (op == OpCodes.Castclass || op == OpCodes.Isinst || op == OpCodes.Box
             || op == OpCodes.Unbox || op == OpCodes.Unbox_Any || op == OpCodes.Ldobj
             || op == OpCodes.Ldind_Ref || op == OpCodes.Ldind_I)
            {
                Push(Pop());
                continue;
            }

            if (op == OpCodes.Ret)
            {
                // A helper that RETURNS a monitored connection or its string is how a service splits the
                // source from the use — `var conn = await OpenMonitoredAsync(server);` — and following it
                // is what lets the analyser narrow the flood-prone argument rule above without losing
                // reach. Synchronous methods hand back here; async ones through SetResult.
                if (Pop()) SetReturnsTainted(r, g);
                stack.Clear();
                continue;
            }

            // ── a method's ADDRESS, taken for a delegate. Everything after this is EXISTING
            //    machinery: `newobj Func<DbConnection>::.ctor` then sees a tainted argument and
            //    IsConnectionCarrying already says yes to Func<DbConnection> through its generic
            //    recursion, so the delegate OBJECT taints; the delegate travels as an argument on the
            //    ordinary path; and at `open()` the callee is Delegate.Invoke, which has no body, so
            //    the `any && IsConnectionCarrying(resultType)` disjunct is what fires. No special case
            //    at the invoke site — one edit instead of two, and no new rule where a call is
            //    resolved. ──
            if (op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
            {
                if (op == OpCodes.Ldvirtftn) Pop();          // the object reference
                var target = Resolve(mod, ins.Token, m);
                bool targetTainted = false;
                if (target != null)
                {
                    g.Methods.TryGetValue((target.Module, target.MetadataToken), out var trec);
                    targetTainted = (trec != null && trec.ReturnsTainted)
                                 || DispatchReturnsTainted(g, target);
                }
                Push(targetTainted);
                continue;
            }

            // ── everything else: keep the simulated stack in step, push nothing tainted ──
            int pops = PopCount(op), pushes = PushCount(op);
            for (int i = 0; i < pops; i++) Pop();
            for (int i = 0; i < pushes; i++) stack.Add(false);
        }

        r.Monitored = monitored;

        // ⚠ THIS WRITE-BACK IS LOAD-BEARING, and leaving it out made the rule half-work.
        // `guardedOut` also turns true when this method READS a connection that an upstream ApplyAsync
        // configured. For an ASYNC method that is the only path there is: the authored method is a stub
        // that stores its arguments into state-machine fields, and all the real work — including every
        // command site — happens in MoveNext, which no caller invokes with arguments in IL, so MoveNext's
        // ArgGuard is never set by anyone. Without this line the knowledge died in a local.
        // MEASURED by the cold gate 2026-09-15: the synchronous hand-down control passed while its async
        // twin was flagged, and in the product DiagnosticScriptRunner.ProcedureExistsAsync — which
        // receives the very connection ExecuteScriptAsync applied the preamble to at :192, and has
        // exactly one caller — sat on the debt list as false debt. Every real hand-down in this codebase
        // is async; the control was written for the mechanism as described rather than for the shape the
        // product actually contains.
        if (guardedOut) r.GuardedByUpstream = true;

        // Taint only ever grows, and every input that can grow it (a parameter, a field this method
        // reads, a callee's return) re-queues this method — so the final walk is the maximal one and
        // this assignment cannot record a count that a later walk would have raised.
        if (sites > r.Sites) r.Sites = sites;
    }

    /// <summary>
    /// A type that holds a LIVE handle to a server — as opposed to a connection STRING, which
    /// <see cref="IsConnectionCarrying"/> also accepts. Only these answer "is this command aimed at a
    /// monitored server?", because a tainted string argument to a command constructor is the SQL text,
    /// not the destination.
    /// </summary>
    private static bool IsLiveConnection(Type? t)
    {
        if (t == null) return false;
        foreach (var c in ConnectionCarrying)
            if (c.IsAssignableFrom(t)) return true;
        return false;
    }

    /// <summary>
    /// Does ANY body the CLR could dispatch to for this call hand a monitored connection back? ONE
    /// helper over ONE map, read at the only two places a callee's return-taint is consulted — the
    /// result of a call, and the target of an <c>ldftn</c>. Deliberately not two copies of the lookup:
    /// a rule that must hold at N sites ships as one shared thing, or it is N things that will drift.
    /// </summary>
    private static bool DispatchReturnsTainted(Graph g, MethodBase called)
        => g.DispatchTargets.TryGetValue((called.Module, called.MetadataToken), out var impls)
           && impls.Any(x => x.ReturnsTainted);

    /// <summary>Records that a method hands a monitored connection back, and wakes everyone who calls it.</summary>
    private static void SetReturnsTainted(MethodRec r, Graph g)
    {
        if (r.ReturnsTainted) return;
        r.ReturnsTainted = true;
        if (g.Callers.TryGetValue((r.Method.Module, r.Method.MetadataToken), out var callers))
            foreach (var c in callers) g.Enqueue(c);
    }

    /// <summary>
    /// Attributes an async state machine's <c>SetResult</c> to the method a developer actually wrote,
    /// because that is the method callers call and the one whose return value carries the connection.
    /// </summary>
    private static void MarkOriginReturnsTainted(MethodRec r, Graph g)
    {
        SetReturnsTainted(r, g);

        var declaring = r.Method.DeclaringType;
        if (declaring == null) return;
        var root = Root(declaring);
        if (root == declaring) return;                       // not a compiler-generated nested machine

        if (!g.ByName.TryGetValue((root, OriginName(r.Method)), out var stubs)) return;
        foreach (var stub in stubs) SetReturnsTainted(stub, g);
    }

    /// <summary>
    /// Can this type hold a live connection to a monitored server? See the remarks on
    /// <see cref="ConnectionCarrying"/> — a string is included because a connection STRING is the source
    /// form, and a row value read back from the server is not, which is the whole point of the split.
    /// </summary>
    private static bool IsConnectionCarrying(Type? t, int depth = 0)
    {
        if (t == null || depth > 4) return false;
        if (t == typeof(string)) return true;
        if (IsLiveConnection(t)) return true;
        if (typeof(System.Data.Common.DbConnectionStringBuilder).IsAssignableFrom(t)) return true;

        // WRAPPERS ARE NOT LAUNDERING. A connection spends most of its life inside something generic —
        // `Task<IDbConnection>`, then `TaskAwaiter<IDbConnection>` across the await, then
        // `(IDbConnection, bool)` out of a rent helper. MEASURED 2026-09-15: without this the ENTIRE
        // alert loop — the six timer-driven reads this whole guard was built for — dropped out of the
        // census, because `await _pool.GetConnectionAsync(connStr)` passes through an awaiter type that
        // is not itself a connection. A census that loses its own founding case is not narrower, it is
        // broken, and the AlertLoopSiteFloor assertion is what said so.
        if (t.IsArray) return IsConnectionCarrying(t.GetElementType(), depth + 1);
        if (t.IsByRef || t.IsPointer) return IsConnectionCarrying(t.GetElementType(), depth + 1);
        if (t.IsGenericType)
            foreach (var a in t.GetGenericArguments())
                if (IsConnectionCarrying(a, depth + 1)) return true;

        return false;
    }

    /// <summary>
    /// Is this call assembling a string rather than reading data? A connection string gets concatenated,
    /// interpolated and appended on its way to <c>new SqlConnection(...)</c>, and taint has to survive
    /// that. <c>DbDataReader.GetString</c> does NOT appear here, and must not.
    /// </summary>
    private static bool IsStringBuilding(MethodBase called)
    {
        var d = called.DeclaringType?.FullName;
        return d == "System.String"
            || d == "System.Text.StringBuilder"
            || d == "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler";
    }

    // ── IL decoding. Same technique as I1ConfigStoreAnalyzer, kept self-contained on purpose so a
    //    change made for that invariant cannot quietly alter this one's reading. ──────────────────

    private static readonly Dictionary<short, OpCode> OpMap = BuildOpMap();

    /// <summary>
    /// The opcode table, built by reflecting over <see cref="OpCodes"/> rather than hand-written. A
    /// hand-written table is a second thing to keep in step with the runtime, and a missing entry
    /// would silently truncate a method's instruction stream and hide whatever came after it.
    /// </summary>
    private static Dictionary<short, OpCode> BuildOpMap()
    {
        var d = new Dictionary<short, OpCode>();
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.FieldType == typeof(OpCode))
            {
                var op = (OpCode)f.GetValue(null)!;
                d[op.Value] = op;
            }
        return d;
    }

    private sealed record Instr(int Offset, OpCode Op, int Token, int Operand, int Next);

    private static List<Instr> Decode(byte[] il)
    {
        var list = new List<Instr>();
        int i = 0;
        while (i < il.Length)
        {
            int start = i;
            short code = il[i];
            if (il[i] == 0xFE && i + 1 < il.Length) { code = unchecked((short)(0xFE00 | il[i + 1])); i += 2; }
            else i += 1;

            if (!OpMap.TryGetValue(code, out var op)) break;

            int token = 0, operand = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: i += 1; break;
                case OperandType.ShortInlineI: operand = il[i]; i += 1; break;
                case OperandType.ShortInlineVar: operand = il[i]; i += 1; break;
                case OperandType.InlineVar: operand = BitConverter.ToUInt16(il, i); i += 2; break;
                case OperandType.InlineBrTarget: i += 4; break;
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    token = BitConverter.ToInt32(il, i); operand = token; i += 4; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch:
                    {
                        int n = BitConverter.ToInt32(il, i); i += 4;
                        i += 4 * n;
                        break;
                    }
                default: i = il.Length; break;
            }
            list.Add(new Instr(start, op, token, operand, i));
        }
        return list;
    }

    /// <summary>
    /// How many values an opcode takes off the stack, from <see cref="OpCode.StackBehaviourPop"/> rather
    /// than a hand-written table — the same reasoning as <see cref="BuildOpMap"/>. Call-shaped opcodes
    /// (<c>Varpop</c>) are handled at the call site, where the callee's signature is resolved.
    /// </summary>
    private static int PopCount(OpCode op) => op.StackBehaviourPop switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
            or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
            or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi
            or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
            or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
        _ => 0,
    };

    private static int PushCount(OpCode op) => op.StackBehaviourPush switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8
            or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        _ => 0,
    };

    private static IEnumerable<MethodBase> DeclaredMethods(Type t)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                             | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MethodBase[] ms, cs;
        try { ms = t.GetMethods(F); } catch { ms = Array.Empty<MethodBase>(); }
        try { cs = t.GetConstructors(F); } catch { cs = Array.Empty<MethodBase>(); }
        return ms.Concat(cs);
    }

    private static byte[]? IlOf(MethodBase m)
    {
        try { return m.GetMethodBody()?.GetILAsByteArray(); } catch { return null; }
    }

    private static MethodBase? Resolve(Module mod, int token, MethodBase ctx)
    {
        try
        {
            Type[]? ta = null, ma = null;
            try { if (ctx.DeclaringType?.IsGenericType == true) ta = ctx.DeclaringType.GetGenericArguments(); } catch { }
            try { if (ctx.IsGenericMethodDefinition) ma = ctx.GetGenericArguments(); } catch { }
            return mod.ResolveMethod(token, ta, ma);
        }
        catch { return null; }
    }

    private static FieldInfo? ResolveField(Module mod, int token, MethodBase ctx)
    {
        try
        {
            Type[]? ta = null, ma = null;
            try { if (ctx.DeclaringType?.IsGenericType == true) ta = ctx.DeclaringType.GetGenericArguments(); } catch { }
            try { if (ctx.IsGenericMethodDefinition) ma = ctx.GetGenericArguments(); } catch { }
            return mod.ResolveField(token, ta, ma);
        }
        catch { return null; }
    }

    private static string KeyOf(Type? declaring, string name) => (declaring?.FullName ?? "?") + "." + name;

    /// <summary>
    /// The name of the method a developer actually wrote. An <c>async</c> method's real IL lives in
    /// <c>MoveNext</c> on a compiler-generated state machine type named <c>&lt;TheMethod&gt;d__47</c>,
    /// and a lambda's in a method named <c>&lt;TheMethod&gt;b__0</c> — so reporting
    /// <see cref="MemberInfo.Name"/> would label two thirds of this census "MoveNext" and make its
    /// entries neither unique nor actionable. This recovers the author's name from the mangled one,
    /// walking outwards so a lambda nested inside an async method still resolves to the async method.
    /// Falls back to the raw name when nothing is mangled.
    /// </summary>
    private static string OriginName(MethodBase m)
    {
        var found = Unmangle(m.Name);
        if (found != null) return found;
        for (var t = m.DeclaringType; t != null; t = t.DeclaringType)
        {
            found = Unmangle(t.Name);
            if (found != null) return found;
        }
        return m.Name;
    }

    /// <summary>
    /// Peels the compiler's angle brackets off a generated name, REPEATEDLY. A lambda inside a local
    /// function inside an async method is mangled more than once — <c>&lt;&lt;TestQuery&gt;b__12_0&gt;d</c> —
    /// and a single peel leaves <c>&lt;TestQuery</c>, which then travels into the frozen debt key as a
    /// name no developer can search for. Measured on <c>Pages/Alerts.razor</c>, 2026-09-15.
    /// </summary>
    private static string? Unmangle(string name)
    {
        // The AUTHORED name is the innermost one, so strip every opening bracket first and then read to
        // the first close. Peeling one bracket at a time matches the INNER '>' and leaves a stray '<'
        // behind: `<<TestQuery>b__12_0>d` came out as `<TestQuery`, a name no developer can grep for,
        // and it would have travelled into the frozen debt key. Measured on Pages/Alerts.razor.
        int i = 0;
        while (i < name.Length && name[i] == '<') i++;
        if (i == 0) return null;
        int end = name.IndexOf('>', i);
        if (end <= i) return null;                       // `<>c__DisplayClass…` carries no author name
        return name.Substring(i, end - i);
    }

    internal static IReadOnlyList<Type> TypesOf(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray()!; }
    }

    /// <summary>The outermost declaring type — so a compiler-generated async state machine counts as
    /// part of the service that declares it, rather than as a type of its own.</summary>
    private static Type Root(Type t)
    {
        var cur = t;
        while (cur.DeclaringType != null) cur = cur.DeclaringType;
        return cur;
    }
}
