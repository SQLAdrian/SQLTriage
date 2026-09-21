/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace SQLTriage.Tests;

/// <summary>
/// Reads THE TEST ASSEMBLY'S OWN IL and answers, for every type in it: does this type reach a read of
/// the PRODUCT assembly's IL or PE metadata, does it reach a numeric assertion, and does it reach the
/// configuration guard? It is the enumerator behind <see cref="ProductCountGuardCensusTests"/>.
///
/// <para><b>WHY STRUCTURE AND NOT A GREP, measured.</b> The brief that started this lane named eight
/// test files as IL walkers because the token <c>MethodBody</c> matched a SOURCE-TEXT helper each of
/// five of them declares (<c>ConnectionRetargetChokepointTests.MethodBody</c>,
/// <c>EvalFailureVisibleTests.ExtractMethodBody</c>, <c>RemediationBatchPreviewUiTests</c>'
/// <c>TryGetMethodBody</c>, and two more). Structurally THREE read IL. This analyser resolves every
/// call, field and type token through <see cref="Module"/> to the member the runtime will actually
/// use, so it cannot confuse a local helper named <c>MethodBody</c> with
/// <see cref="System.Reflection.MethodBody"/> — which is precisely the error it exists to stop
/// repeating. A character census would also have missed the three CONSUMERS that pin the numbers,
/// because the file that walks the IL and the file that pins the count are different files.</para>
///
/// <para><b>THE CALL GRAPH IS WHY IT WORKS AT ALL.</b> The analysers hold no assertions and the census
/// classes hold no IL primitives, so no single method is both the reader and the pinner. Each flag is
/// therefore propagated BACKWARDS along resolved call edges inside this assembly until it stops
/// moving: a type "reaches" an IL read if anything it calls, transitively, performs one. That is also
/// what lets one shared guard satisfy three consumers — a consumer reaches the configuration read
/// through <see cref="ProductAssemblyBuildConfiguration"/> exactly as it reaches the IL read through
/// an analyser.</para>
///
/// <para><b>⚠ ITS OWN IL PRIMITIVES, deliberately.</b> The house convention — written down at
/// <c>CheckBadgeClassificationTests.CalledMemberNames</c> and on <c>SessionSafetyAnalyzer</c> — is
/// that each analyser keeps its own copy, so a change made for one invariant cannot quietly alter
/// another's reading. Nothing here is shared with any other analyser and nothing here was refactored
/// into them; their readings are byte-identical to what they were before this lane.</para>
///
/// <para><b>WHAT IT CANNOT SEE, said plainly.</b> (1) Reflection that goes through a string —
/// <c>Type.GetType("...")</c>, <c>Activator</c>, a delegate stored in a field — is an edge it does
/// not follow, so a consumer that calls its analyser through indirection would read as unguarded
/// (fail-LOUD, the safe direction) and a guard called through indirection would read as absent (also
/// fail-loud). (2) It reads THIS assembly's IL, so it says nothing about a census living in another
/// test project. (3) It classifies APIs by resolved declaring type, so a future rename inside
/// <c>System.Reflection</c> would silently narrow it — which is why the census asserts specimen
/// controls rather than trusting the classifier.</para>
/// </summary>
internal static class ProductCountGuardReachAnalyzer
{
    // ── Its own IL primitives ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The opcode table, built by reflecting over <see cref="OpCodes"/> rather than hand-written, so
    /// it is the runtime's own table and a mistyped constant cannot narrow the walk.
    /// </summary>
    private static readonly Dictionary<ushort, OpCode> OpTable = BuildOpTable();

    private static Dictionary<ushort, OpCode> BuildOpTable()
    {
        var table = new Dictionary<ushort, OpCode>();
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.GetValue(null) is OpCode op)
                table[(ushort)op.Value] = op;
        return table;
    }

    private readonly record struct Instr(OpCode Op, int Token);

    /// <summary>
    /// Decodes instructions and their operand widths. On an unknown opcode byte it STOPS rather than
    /// inventing a width, because a mis-stepped decoder silently reads operands as opcodes and the
    /// result is a quiet undercount — the failure mode this whole lane is about.
    /// </summary>
    private static List<Instr> Decode(byte[] il)
    {
        var list = new List<Instr>();
        int i = 0;
        while (i < il.Length)
        {
            ushort v = il[i++];
            if (v == 0xFE && i < il.Length) v = (ushort)(0xFE00 | il[i++]);
            if (!OpTable.TryGetValue(v, out var op)) break;
            int token = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineI:
                case OperandType.ShortInlineR: i += 4; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: i += 8; break;
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                case OperandType.InlineString:
                case OperandType.InlineSig:
                    if (i + 4 > il.Length) return list;
                    token = BitConverter.ToInt32(il, i); i += 4; break;
                case OperandType.InlineSwitch:
                    if (i + 4 > il.Length) return list;
                    int n = BitConverter.ToInt32(il, i); i += 4 + 4 * n; break;
                default: return list;
            }
            list.Add(new Instr(op, token));
        }
        return list;
    }

    // ── What the census asks about ────────────────────────────────────────────────────────────────

    /// <summary>One type's reading, with its own counts AND its transitive reach kept separate.</summary>
    internal sealed class TypeReach
    {
        public string TypeName = "";
        public bool IsTestType;                 // declares at least one [Fact]/[Theory] method
        public int Methods;

        // Direct, in the type's own methods (nested compiler-generated types included).
        public int OwnIlReads, OwnPeReads, OwnProductRefs, OwnConfigReads, OwnNumericPins, OwnReflectiveEnumerations;

        // Transitive, following resolved call edges inside this assembly.
        public bool ReachesIlRead, ReachesPeRead, ReachesProductReference,
                    ReachesConfigurationRead, ReachesNumericPin, ReachesReflectiveEnumeration;

        // Which method supplied the transitive flag — so a failure can say HOW a type qualifies.
        public string? IlReadWitness, PeReadWitness, ConfigurationReadWitness, NumericPinWitness;

        public readonly SortedSet<string> IlApis = new(StringComparer.Ordinal);
        public readonly SortedSet<string> PeApis = new(StringComparer.Ordinal);
        public readonly SortedSet<string> ConfigApis = new(StringComparer.Ordinal);
        public readonly SortedSet<string> NumericApis = new(StringComparer.Ordinal);

        /// <summary>
        /// A type is IN THE POPULATION of this invariant when it is a test type whose verdict can rest
        /// on a number read out of the PRODUCT assembly's IL or PE metadata. Both halves are required:
        /// a type that walks IL but never touches the product assembly (this analyser's own census,
        /// which walks the TEST assembly) is measuring something whose numbers were never frozen
        /// against a product configuration, and a type that touches the product without reading its
        /// IL or metadata cannot have a count that moves with codegen.
        /// </summary>
        public bool IsPopulation => IsTestType && ReachesProductReference && (ReachesIlRead || ReachesPeRead);

        public string Line =>
            $"TYPE {TypeName} test={IsTestType} methods={Methods} "
            + $"own[il={OwnIlReads} pe={OwnPeReads} product={OwnProductRefs} config={OwnConfigReads} "
            + $"numeric={OwnNumericPins} reflect={OwnReflectiveEnumerations}] "
            + $"reaches[il={ReachesIlRead} pe={ReachesPeRead} product={ReachesProductReference} "
            + $"config={ReachesConfigurationRead} numeric={ReachesNumericPin} reflect={ReachesReflectiveEnumeration}] "
            + $"POPULATION={IsPopulation}";
    }

    internal sealed class Report
    {
        public readonly List<TypeReach> Types = new();
        public int TypesSeen, MethodBodiesDecoded, InstructionsDecoded, TokensResolved, UnresolvedTokens, CallEdges;
        public string ProductAssembly = "";
        public int ProductTypesSeen;

        public TypeReach? this[string typeName] =>
            Types.FirstOrDefault(t => string.Equals(t.TypeName, typeName, StringComparison.Ordinal));

        public IEnumerable<TypeReach> Population => Types.Where(t => t.IsPopulation);
    }

    // ── The walk ──────────────────────────────────────────────────────────────────────────────────

    private sealed class MethodFlags
    {
        public bool Il, Pe, Product, Config, Numeric, Reflect;

        // DIRECT hit counts, in this method's own body. Kept separate from the booleans above
        // because Propagate() sets those on every CALLER, so summing them would report a
        // consumer as having read IL itself. These are what the census's negative control
        // measures: a type whose own body performs zero IL reads but whose NAME matches.
        public int IlCount, PeCount, ProductCount, ConfigCount, NumericCount, ReflectCount;
        public string Name = "";
        public Type? Outer;
        public readonly List<int> Callees = new();

        // The resolved API names behind each flag, kept per method so the census can print WHICH call
        // was matched. A classification nobody can audit is an assertion, not a measurement.
        public readonly SortedSet<string> IlApis = new(StringComparer.Ordinal);
        public readonly SortedSet<string> PeApis = new(StringComparer.Ordinal);
        public readonly SortedSet<string> ConfigApis = new(StringComparer.Ordinal);
        public readonly SortedSet<string> NumericApis = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Walks every method body in <paramref name="testAssembly"/>, classifies the resolved tokens,
    /// propagates each flag backwards along call edges to a fixpoint, and aggregates per OUTERMOST
    /// type — so a lambda's closure class, an async state machine and a nested helper all count as the
    /// code of the type that declares them, which is where the next reader will be standing.
    /// </summary>
    internal static Report Read(Assembly testAssembly, Assembly productAssembly)
    {
        var report = new Report
        {
            ProductAssembly = productAssembly.GetName().Name ?? "(unnamed)",
            ProductTypesSeen = SafeTypes(productAssembly).Length,
        };

        var byToken = new Dictionary<int, MethodFlags>();
        var testModules = new HashSet<Module>(SafeTypes(testAssembly).Select(t => t.Module));

        foreach (var m in AllMethods(testAssembly))
        {
            byte[]? il;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); }
            catch { il = null; }
            if (il is null) continue;

            report.MethodBodiesDecoded++;
            var flags = new MethodFlags
            {
                Name = (m.DeclaringType?.FullName ?? "?") + "." + m.Name,
                Outer = Outermost(m.DeclaringType),
            };
            byToken[m.MetadataToken] = flags;

            var mod = m.Module;
            var typeArgs = SafeGenericArgs(m.DeclaringType);
            var methodArgs = (m as MethodInfo)?.IsGenericMethodDefinition == true
                ? SafeGenericArgs(m as MethodInfo) : null;

            foreach (var ins in Decode(il))
            {
                report.InstructionsDecoded++;
                if (ins.Token == 0) continue;
                var ot = ins.Op.OperandType;
                if (ot != OperandType.InlineMethod && ot != OperandType.InlineField
                    && ot != OperandType.InlineType && ot != OperandType.InlineTok) continue;

                MemberInfo? member = null;
                try
                {
                    member = ot switch
                    {
                        OperandType.InlineMethod => mod.ResolveMethod(ins.Token, typeArgs, methodArgs),
                        OperandType.InlineField => mod.ResolveField(ins.Token, typeArgs, methodArgs),
                        OperandType.InlineType => mod.ResolveType(ins.Token, typeArgs, methodArgs),
                        _ => mod.ResolveMember(ins.Token, typeArgs, methodArgs),
                    };
                }
                catch { report.UnresolvedTokens++; continue; }
                if (member is null) { report.UnresolvedTokens++; continue; }
                report.TokensResolved++;

                Classify(member, flags, testModules, productAssembly);
            }
        }

        // Fixpoint. Each flag travels from callee to caller until nothing moves. It is a worklist,
        // not a repeated sweep, so a call CYCLE terminates: a method is enqueued only on the pass
        // that first sets its flag. An empty call graph would silently collapse every 'reaches'
        // answer to the direct answer, which is why the census asserts the edge count before it
        // believes any result below.
        var callers = new Dictionary<int, List<int>>();
        foreach (var (token, f) in byToken)
            foreach (var callee in f.Callees)
            {
                if (!callers.TryGetValue(callee, out var list)) callers[callee] = list = new List<int>();
                list.Add(token);
            }

        Propagate(byToken, callers, f => f.Il, (f, v) => f.Il = v);
        Propagate(byToken, callers, f => f.Pe, (f, v) => f.Pe = v);
        Propagate(byToken, callers, f => f.Product, (f, v) => f.Product = v);
        Propagate(byToken, callers, f => f.Config, (f, v) => f.Config = v);
        Propagate(byToken, callers, f => f.Numeric, (f, v) => f.Numeric = v);
        Propagate(byToken, callers, f => f.Reflect, (f, v) => f.Reflect = v);

        // Aggregate per outermost type, including every type with no methods at all (zeros printed).
        var perType = new Dictionary<Type, TypeReach>();
        foreach (var t in SafeTypes(testAssembly))
        {
            var outer = Outermost(t);
            if (outer is null || perType.ContainsKey(outer)) continue;
            perType[outer] = new TypeReach
            {
                TypeName = outer.FullName ?? outer.Name,
                IsTestType = DeclaresAFact(outer),
            };
        }
        report.TypesSeen = perType.Count;

        foreach (var (token, f) in byToken)
        {
            if (f.Outer is null || !perType.TryGetValue(f.Outer, out var tr)) continue;
            tr.Methods++;
            tr.OwnIlReads += f.IlCount;
            tr.OwnPeReads += f.PeCount;
            tr.OwnProductRefs += f.ProductCount;
            tr.OwnConfigReads += f.ConfigCount;
            tr.OwnNumericPins += f.NumericCount;
            tr.OwnReflectiveEnumerations += f.ReflectCount;
            if (f.Il) { tr.ReachesIlRead = true; tr.IlReadWitness ??= WitnessOf(f, byToken, x => x.Il); }
            if (f.Pe) { tr.ReachesPeRead = true; tr.PeReadWitness ??= WitnessOf(f, byToken, x => x.Pe); }
            if (f.Product) tr.ReachesProductReference = true;
            if (f.Config) { tr.ReachesConfigurationRead = true; tr.ConfigurationReadWitness ??= WitnessOf(f, byToken, x => x.Config); }
            if (f.Numeric) { tr.ReachesNumericPin = true; tr.NumericPinWitness ??= WitnessOf(f, byToken, x => x.Numeric); }
            if (f.Reflect) tr.ReachesReflectiveEnumeration = true;

            Merge(tr.IlApis, f.IlApis);
            Merge(tr.PeApis, f.PeApis);
            Merge(tr.ConfigApis, f.ConfigApis);
            Merge(tr.NumericApis, f.NumericApis);
        }

        report.CallEdges = byToken.Values.Sum(f => f.Callees.Count);
        report.Types.AddRange(perType.Values.OrderBy(t => t.TypeName, StringComparer.Ordinal));
        return report;
    }

    /// <summary>
    /// A readable witness for a transitive flag: the nearest method in the closure that carries it,
    /// so a failure message can say "reaches the configuration read via X" instead of asserting it.
    /// </summary>
    private static string WitnessOf(MethodFlags f, Dictionary<int, MethodFlags> byToken, Func<MethodFlags, bool> has)
    {
        foreach (var callee in f.Callees)
            if (byToken.TryGetValue(callee, out var c) && has(c) && !ReferenceEquals(c, f))
                return c.Name;
        return f.Name + " (directly)";
    }

    private static void Propagate(Dictionary<int, MethodFlags> byToken, Dictionary<int, List<int>> callers,
                                  Func<MethodFlags, bool> get, Action<MethodFlags, bool> set)
    {
        var queue = new Queue<int>(byToken.Where(kv => get(kv.Value)).Select(kv => kv.Key));
        while (queue.Count > 0)
        {
            var token = queue.Dequeue();
            if (!callers.TryGetValue(token, out var cs)) continue;
            foreach (var caller in cs)
                if (byToken.TryGetValue(caller, out var f) && !get(f)) { set(f, true); queue.Enqueue(caller); }
        }
    }

    // ── Classification, all of it off resolved metadata ───────────────────────────────────────────

    private static void Classify(MemberInfo member, MethodFlags flags, HashSet<Module> testModules,
                                 Assembly product)
    {
        var declaring = member is Type asType ? asType : member.DeclaringType;
        if (declaring is null) return;
        var def = declaring.IsGenericType && !declaring.IsGenericTypeDefinition
            ? declaring.GetGenericTypeDefinition() : declaring;
        string ns = def.Namespace ?? "";
        string full = def.FullName ?? (ns + "." + def.Name);
        string name = member.Name;
        string api = full + "::" + name;

        // (1) Call edges inside this assembly, which is what makes the reach transitive.
        if (member is MethodBase callee && testModules.Contains(callee.Module))
        {
            var target = callee is MethodInfo mi && mi.IsGenericMethod && !mi.IsGenericMethodDefinition
                ? mi.GetGenericMethodDefinition() : callee;
            flags.Callees.Add(target.MetadataToken);
        }

        // (2) READS PRODUCT IL. The product's IL is read through MethodBase.GetMethodBody /
        //     MethodBody.GetILAsByteArray, decoded against System.Reflection.Emit.OpCodes, and its
        //     tokens resolved through Module.Resolve*. Any of those is the technique.
        //     ⚠ EVERY RULE BELOW IS QUALIFIED BY THE DECLARING TYPE, never by the member name alone.
        //     A rule reading `name == "GetMethodBody"` would be a character census wearing structural
        //     clothes: five test files in this assembly declare their own source-text helper called
        //     MethodBody / ExtractMethodBody / TryGetMethodBody / ReadMethodBody, and matching on the
        //     name is exactly how the brief for this lane came to list eight IL walkers where there
        //     are three.
        if (full == "System.Reflection.MethodBody"
            || (ns == "System.Reflection" && name == "GetMethodBody")
            || full == "System.Reflection.Emit.OpCodes"
            || full == "System.Reflection.Emit.OpCode"
            || (full == "System.Reflection.Module"
                && (name == "ResolveMethod" || name == "ResolveField" || name == "ResolveType"
                    || name == "ResolveMember" || name == "ResolveSignature")))
        { flags.Il = true; flags.IlCount++; AddApi(flags.IlApis, api); }

        // (3) READS PE METADATA. The embedded-source census reads the portable PDB and the PE debug
        //     directory, which is configuration-specific for a different reason than IL: Release
        //     EMBEDS the PDB (155 source documents, 1,851,736 bytes, measured) and Debug ships it as
        //     a separate file, so every row count this reads is zero in Debug.
        if (ns.StartsWith("System.Reflection.Metadata", StringComparison.Ordinal)
            || ns.StartsWith("System.Reflection.PortableExecutable", StringComparison.Ordinal))
        { flags.Pe = true; flags.PeCount++; AddApi(flags.PeApis, api); }

        // (4) TOUCHES THE PRODUCT ASSEMBLY. Any resolved member declared in it, which is how a
        //     consumer's own typeof(ConfigFileHelper) qualifies it.
        try { if (declaring.Assembly == product) { flags.Product = true; flags.ProductCount++; } }
        catch { /* a member whose assembly cannot be read is not evidence either way */ }

        // (5) READS THE BUILD CONFIGURATION. DebuggableAttribute is the only thing on an assembly that
        //     discriminates Debug from Release, so reaching it IS reaching the guard — whether through
        //     the shared guard or through a class's own equivalent. Detected two ways because it is
        //     reachable two ways: as a generic argument (GetCustomAttribute<DebuggableAttribute>) and
        //     as a type token (typeof(DebuggableAttribute)).
        if (def == typeof(DebuggableAttribute)) { flags.Config = true; flags.ConfigCount++; AddApi(flags.ConfigApis, api); }
        if (member is MethodBase gm)
        {
            Type[]? args = null;
            try { args = gm is MethodInfo g && g.IsGenericMethod ? g.GetGenericArguments() : null; }
            catch { }
            if (args != null && args.Any(a => a == typeof(DebuggableAttribute)))
            { flags.Config = true; flags.ConfigCount++; AddApi(flags.ConfigApis, api + "<DebuggableAttribute>"); }
        }

        // (6) PINS A NUMBER. FluentAssertions' numeric assertions, the collection COUNT assertions,
        //     and xunit's numeric Equal/InRange/Single. Emptiness (BeEmpty, NotBeEmpty, Assert.Empty)
        //     is deliberately NOT a pin: it is a liveness or an invariant, not a frozen magnitude, and
        //     a census whose only numbers are "non-empty" cannot drift with codegen.
        if (ns == "FluentAssertions.Numeric"
            || (ns.StartsWith("FluentAssertions", StringComparison.Ordinal)
                && (name.StartsWith("HaveCount", StringComparison.Ordinal) || name == "HaveSameCount")))
        { flags.Numeric = true; flags.NumericCount++; AddApi(flags.NumericApis, api); }
        if (full == "Xunit.Assert" && (name == "Equal" || name == "NotEqual" || name == "InRange"
                                       || name == "NotInRange" || name == "Single"))
        {
            bool numeric = name == "Single";
            if (member is MethodBase am)
            {
                try
                {
                    numeric |= am.GetParameters().Any(p => IsNumeric(p.ParameterType));
                    if (am is MethodInfo ami && ami.IsGenericMethod)
                        numeric |= ami.GetGenericArguments().Any(IsNumeric);
                }
                catch { }
            }
            if (numeric) { flags.Numeric = true; flags.NumericCount++; AddApi(flags.NumericApis, api); }
        }

        // (7) The WIDER boundary, recorded rather than asserted on: reflective enumeration of an
        //     assembly. Measured 2026-09-16: Debug-full carries 3,849 types and 29,570 methods with
        //     IL against Release-full's 3,825 and 28,444 — so a pinned REFLECTED count is
        //     configuration-specific too. 51 of 506 test files reach these APIs. The census prints
        //     them and asserts nothing about them; widening is a separate, deliberate decision.
        if ((full == "System.Reflection.Assembly"
                && (name == "GetTypes" || name == "GetExportedTypes" || name == "get_Location"))
            || (full == "System.Type"
                && (name == "GetMethods" || name == "GetMembers" || name == "GetProperties"
                    || name == "GetFields" || name == "GetConstructors" || name == "GetNestedTypes")))
            { flags.Reflect = true; flags.ReflectCount++; }
    }

    private static void AddApi(SortedSet<string> set, string api) { if (set.Count < 24) set.Add(api); }

    private static void Merge(SortedSet<string> into, SortedSet<string> from)
    {
        foreach (var api in from) { if (into.Count >= 40) return; into.Add(api); }
    }

    private static bool IsNumeric(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
        || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte)
        || t == typeof(double) || t == typeof(float) || t == typeof(decimal)
        || t == typeof(nint) || t == typeof(nuint);

    // ── Reflection plumbing, every failure reported rather than swallowed ─────────────────────────

    private static Type[] SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).Select(t => t!).ToArray(); }
    }

    private static Type[]? SafeGenericArgs(MemberInfo? m)
    {
        try
        {
            return m switch
            {
                Type t => t.IsGenericType ? t.GetGenericArguments() : null,
                MethodInfo mi => mi.IsGenericMethodDefinition ? mi.GetGenericArguments() : null,
                _ => null,
            };
        }
        catch { return null; }
    }

    private static IEnumerable<MethodBase> AllMethods(Assembly asm)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                             | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var t in SafeTypes(asm))
        {
            MethodBase[] ms;
            try { ms = t.GetMethods(F).Cast<MethodBase>().Concat(t.GetConstructors(F)).ToArray(); }
            catch { continue; }
            foreach (var m in ms) yield return m;
        }
    }

    /// <summary>
    /// The outermost enclosing type. A lambda lives in a nested <c>&lt;&gt;c</c> closure class and an
    /// async method's body lives in a nested state machine, so without this a guard called from
    /// inside a lambda would read as absent from the type that declares it.
    /// </summary>
    private static Type? Outermost(Type? t)
    {
        while (t?.DeclaringType != null) t = t.DeclaringType;
        return t;
    }

    private static bool DeclaresAFact(Type t)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                             | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        try
        {
            foreach (var m in t.GetMethods(F))
                if (m.GetCustomAttributes(typeof(Xunit.FactAttribute), inherit: true).Length > 0)
                    return true;
            foreach (var nested in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (DeclaresAFact(nested)) return true;
        }
        catch { }
        return false;
    }
}
