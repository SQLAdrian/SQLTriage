/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SQLTriage.Tests;

/// <summary>
/// Reads a COMPILED ASSEMBLY — metadata and IL — and answers two questions about the invariant
/// <b>(I1) a customer's configuration must never be silently replaced by a built-in default</b>:
/// which types are config stores, and which of their methods can reach a file-REPLACING write from a
/// handler that caught a failed config read.
///
/// <para><b>WHY THIS DOES NOT READ SOURCE TEXT.</b> Every other census in this repo
/// (<c>RuntimeWriteFolderCensusTests</c> at :196, :464, :493) opens <c>.cs</c> files and matches
/// characters. Under the rule Adrian sharpened on 2026-09-11 that makes each of them a TRIPWIRE and not
/// a guarantee: a regex measures the pattern, not the code, so a reformat, a rename, a line break or an
/// extracted helper walks through it and the test stays green. This one enumerates
/// <see cref="Assembly.GetTypes"/>, walks <see cref="MethodBody.GetILAsByteArray"/> and resolves every
/// call through <see cref="Module.ResolveMethod(int, Type[], Type[])"/>, so a "call" here is the method
/// the runtime will actually invoke. It is immune to formatting entirely and needs no new dependency —
/// there is no Roslyn in this test project and this deliberately does not add one.</para>
///
/// <para><b>⚠ THE ONE PLACE IT STILL READS A STRING, NAMED SO IT IS NOT MISTAKEN FOR STRUCTURE.</b>
/// <see cref="ConfigStoreTypes"/> recognises a config store partly by the literal <c>"Config"</c>,
/// resolved from the assembly's user-string heap via <see cref="Module.ResolveString"/>. That is the
/// literal the running code actually passes to <c>Path.Combine</c>, not a source-text match, so
/// comments, formatting and renames cannot fool it — but a store that builds its folder some other way
/// would be invisible to that half of the predicate.</para>
///
/// <para><b>⚠ THE ILLUSTRATION THIS COMMENT ORIGINALLY GAVE WAS WRONG, and the cold gate MEASURED the
/// real hole on 2026-09-12.</b> It said a <c>static readonly</c> holding "Config" escapes because it is
/// "NOT inlined into IL". On the store ITSELF that case is CAUGHT: the <c>ldstr</c> lives in that type's
/// <c>.cctor</c>, and the declared-method walk includes static constructors. A <c>const</c> in another
/// type is also caught, because a const IS inlined at the use site. What ACTUALLY escapes is the literal
/// moving to ANOTHER type as a <c>static readonly</c> - the ordinary "extract a constants class"
/// refactor - and a folder built from <c>Environment.GetEnvironmentVariable</c>.
///
/// <b>And when a store escapes this way it leaves the census ENTIRELY</b>, because the reachability half
/// only ever examines types this predicate returned: there is no second chance unless the store routes
/// through <see cref="SQLTriage.Data.ConfigFileHelper"/>. MEASURED with the <c>ldstr</c> half disabled:
/// the store set falls from 29 to 14 on the community axis, so <b>15 of 29 stores hang on the literal
/// alone</b>. The three stores whose omission cost an operator their data on 2026-08-04 are all in the
/// helper-routed 14 and survive that refactor; <c>LicenseService</c> and <c>SeatRegister</c>, the two
/// declared exemptions, do not.</para>
///
/// <para>The other half — any call to
/// <see cref="SQLTriage.Data.ConfigFileHelper"/> — is a resolved method token with no such hole, and a
/// store routed through the house helper is caught however it built its path.</para>
///
/// <para><b>⚠ A COUNT MEASURED OFF IL IS SPECIFIC TO THE BUILD CONFIGURATION IT WAS MEASURED IN.</b>
/// PROVED 2026-09-16 at <c>7b997b4</c>: the S-1 session-safety census, which walks IL the same way
/// this does, reads 120 command sites in Release-full and 117 in Debug-full at the IDENTICAL commit,
/// because Roslyn MERGES hoisted async state-machine fields for same-typed locals when optimisations
/// are ON and keeps one field per authored local when they are OFF — and a taint set keyed by field
/// token cannot tell two variables sharing one field apart. THIS enumerator was measured NOT to move
/// (30 store types in Release-full and Debug-full, 29 in both community cells, 3 findings in all
/// four), so the property is about the technique, not about today's numbers. Any test pinning a count
/// from here must call
/// <see cref="ProductAssemblyBuildConfiguration.RequireTheBuildConfigurationTheseNumbersWereFrozenIn"/>
/// first, as <see cref="ConfigStoreI1CensusTests.Census_is_recorded_for_the_gate"/> does; asserting a
/// SET rather than a COUNT needs no guard at all. That choice is ENFORCED by
/// <see cref="ProductCountGuardCensusTests"/>, which enumerates the consumers of this reader from the
/// compiled test assembly's IL rather than from a list anyone maintains.</para>
/// </summary>
internal static class I1ConfigStoreAnalyzer
{
    // ── The sinks. Keyed by declaring type + method name, resolved from metadata tokens. ──────────

    /// <summary>
    /// Writes that REPLACE the bytes at a path. <c>File.Copy</c> and <c>File.Move</c> are deliberately
    /// absent: they are how this codebase PRESERVES (<c>.rejected-</c>, <c>.corrupt.</c>,
    /// <c>KeyAsideLifecycle.SetAside</c>), so counting them made the census flag the quarantine that
    /// keeps an operator's damaged file — measured 2026-09-12, 6 hits with them against 4 without, and
    /// the two extra were <c>ConfigFileHelper.QuarantineUnreadableFile</c> and
    /// <c>DashboardConfigService</c>'s <c>.corrupt</c> copy, both the right behaviour.
    /// </summary>
    internal static readonly HashSet<string> ReplacingWrites = new(StringComparer.Ordinal)
    {
        "System.IO.File.WriteAllText", "System.IO.File.WriteAllTextAsync",
        "System.IO.File.WriteAllBytes", "System.IO.File.WriteAllBytesAsync",
        "System.IO.File.WriteAllLines", "System.IO.File.WriteAllLinesAsync",
        "System.IO.File.Replace", "System.IO.File.Create", "System.IO.File.CreateText",
        "System.IO.FileStream..ctor", "System.IO.StreamWriter..ctor",
        "SQLTriage.Data.ConfigFileHelper.Save",
    };

    /// <summary>Any write at all — the wider set, used only to decide whether a type is a store.</summary>
    internal static readonly HashSet<string> AnyWrite = new(ReplacingWrites, StringComparer.Ordinal)
    {
        "System.IO.File.Copy", "System.IO.File.Move",
        "System.IO.File.AppendAllText", "System.IO.File.AppendAllTextAsync", "System.IO.File.OpenWrite",
    };

    /// <summary>Getting bytes off disk, or turning bytes into a config object.</summary>
    internal static readonly HashSet<string> Reads = new(StringComparer.Ordinal)
    {
        "System.IO.File.ReadAllText", "System.IO.File.ReadAllTextAsync",
        "System.IO.File.ReadAllBytes", "System.IO.File.ReadAllBytesAsync",
        "System.IO.File.ReadAllLines", "System.IO.File.ReadAllLinesAsync",
        "System.IO.File.OpenRead", "System.IO.File.OpenText", "System.IO.File.Open",
        "System.IO.StreamReader..ctor",
        "System.Text.Json.JsonSerializer.Deserialize",
        "SQLTriage.Data.ConfigFileHelper.Load", "SQLTriage.Data.ConfigFileHelper.InspectStore",
    };

    private const string ConfigFolderLiteral = "Config";
    private const string ConfigHelper = "SQLTriage.Data.ConfigFileHelper";

    /// <summary>One method that can write after a failed read, with the IL offsets that prove it.</summary>
    internal sealed record Finding(MethodBase Method, string DisplayName, int CatchOffset,
                                   int WriteOffset, string WriteCall)
    {
        public override string ToString() =>
            $"{DisplayName}  catch@IL_{CatchOffset:X4} -> write@IL_{WriteOffset:X4} {WriteCall}";
    }

    // ── IL decoding ───────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<short, OpCode> OpMap = BuildOpMap();

    /// <summary>
    /// The opcode table, built by reflecting over <see cref="OpCodes"/> rather than hand-written. A
    /// hand-written table is a second thing to keep in step with the runtime, and a missing entry would
    /// silently truncate a method's instruction stream and hide whatever came after it.
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

    /// <summary>One decoded instruction. <c>Targets</c> is empty unless the instruction branches.</summary>
    private sealed record Instr(int Offset, OpCode Op, int Token, int Next, int[] Targets);

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

            int token = 0;
            int[] targets = Array.Empty<int>();
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                    { sbyte d = unchecked((sbyte)il[i]); i += 1; targets = new[] { i + d }; break; }
                case OperandType.InlineBrTarget:
                    { int d = BitConverter.ToInt32(il, i); i += 4; targets = new[] { i + d }; break; }
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    token = BitConverter.ToInt32(il, i); i += 4; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch:
                    {
                        int n = BitConverter.ToInt32(il, i); i += 4;
                        var deltas = new int[n];
                        for (int k = 0; k < n; k++) { deltas[k] = BitConverter.ToInt32(il, i); i += 4; }
                        targets = deltas.Select(d => i + d).ToArray();
                        break;
                    }
                default: i = il.Length; break;
            }
            list.Add(new Instr(start, op, token, i, targets));
        }
        return list;
    }

    private static IEnumerable<MethodBase> DeclaredMethods(Type t)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                             | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MethodBase[] ms, cs;
        try { ms = t.GetMethods(F); } catch { ms = Array.Empty<MethodBase>(); }
        try { cs = t.GetConstructors(F); } catch { cs = Array.Empty<MethodBase>(); }
        return ms.Concat(cs);
    }

    private static MethodBody? BodyOf(MethodBase m)
    {
        try { return m.GetMethodBody(); } catch { return null; }
    }

    private static byte[]? IlOf(MethodBase m)
    {
        try { return BodyOf(m)?.GetILAsByteArray(); } catch { return null; }
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

    private static string Key(MethodBase m) => (m.DeclaringType?.FullName ?? "?") + "." + m.Name;

    internal static IReadOnlyList<Type> TypesOf(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray()!; }
    }

    /// <summary>The outermost declaring type — so a compiler-generated state machine or closure counts
    /// as part of the store that declares it, rather than as a type of its own.</summary>
    private static Type Root(Type t)
    {
        var cur = t;
        while (cur.DeclaringType != null) cur = cur.DeclaringType;
        return cur;
    }

    // ── Part A: which types are config stores ─────────────────────────────────────────────────────

    /// <summary>
    /// Every type in <paramref name="asm"/> that both names the config folder (or routes through
    /// <see cref="SQLTriage.Data.ConfigFileHelper"/>) and puts bytes on disk. Nested and
    /// compiler-generated types are folded into their outermost declaring type.
    ///
    /// <para>This is the ADDITION guard's enumerator, and the reason it is stated at type level is
    /// the 2026-08-04 incident: three stores were not on the hand-kept list at all, so the question
    /// that had to become mechanical was "what is a config store", not "did someone remember".</para>
    /// </summary>
    internal static SortedSet<string> ConfigStoreTypes(Assembly asm)
    {
        var namesConfig = new HashSet<Type>();
        var writes = new HashSet<Type>();

        foreach (var t in TypesOf(asm))
        {
            if (t.FullName == null) continue;
            var mod = t.Module;
            var root = Root(t);
            foreach (var m in DeclaredMethods(t))
            {
                var il = IlOf(m);
                if (il == null) continue;
                foreach (var ins in Decode(il))
                {
                    if (ins.Op == OpCodes.Ldstr)
                    {
                        string? s = null;
                        try { s = mod.ResolveString(ins.Token); } catch { }
                        if (s == ConfigFolderLiteral) namesConfig.Add(root);
                    }
                    else if (ins.Op == OpCodes.Call || ins.Op == OpCodes.Callvirt || ins.Op == OpCodes.Newobj)
                    {
                        var c = Resolve(mod, ins.Token, m);
                        if (c == null) continue;
                        if (AnyWrite.Contains(Key(c))) writes.Add(root);
                        if (c.DeclaringType?.FullName == ConfigHelper) namesConfig.Add(root);
                    }
                }
            }
        }

        return new SortedSet<string>(
            namesConfig.Intersect(writes).Select(t => t.FullName!), StringComparer.Ordinal);
    }

    // ── Part B: can a failed read reach a replacing write ─────────────────────────────────────────

    /// <summary>
    /// Every method on a config-store type where a REPLACING write is reachable, along the method's own
    /// control flow, from a catch handler whose guarded region performed a config READ.
    ///
    /// <para><b>That is the exact shape of the defect this lane fixed.</b>
    /// <c>ReportPageConfigService.Load</c> read the file inside a <c>try</c>, logged in the
    /// <c>catch</c>, and then fell out of the handler into <c>SaveRoot(BuildDefaults())</c> — so the
    /// write was not IN the handler, it was what the handler's <c>leave</c> landed on. A census that
    /// only looked inside handlers would have passed it, and did: measured 2026-09-12, 3 hits and this
    /// method not among them.</para>
    ///
    /// <para><b>Reachability is intra-method plus a same-type callee closure.</b> The write that costs
    /// the data is usually one level down, in the store's own private <c>SaveX</c>, so a method that
    /// transitively reaches a replacing write through methods OF THE SAME TYPE counts as a write. The
    /// closure stops at the type boundary on purpose: taking it across the whole assembly pulled in
    /// every audit-log call made from a catch and went from 25 hits to 54, none of the extra ones about
    /// configuration at all.</para>
    /// </summary>
    internal static List<Finding> FailedReadReachesReplacingWrite(Assembly asm)
    {
        var stores = ConfigStoreTypes(asm);
        var stateMachineOwner = StateMachineOwners(asm);
        var findings = new List<Finding>();

        foreach (var group in TypesOf(asm)
                     .Where(t => t.FullName != null && stores.Contains(Root(t).FullName!))
                     .GroupBy(t => Root(t).FullName!, StringComparer.Ordinal))
        {
            var types = group.ToList();
            var members = types.SelectMany(DeclaredMethods).ToList();
            var inGroup = new HashSet<Type>(types);

            // Same-group transitive closures: which members reach a replacing write / a read.
            var reachesWrite = new HashSet<int>();
            var reachesRead = new HashSet<int>();
            var edges = new Dictionary<int, List<int>>();

            foreach (var m in members)
            {
                var il = IlOf(m);
                if (il == null) continue;
                var mod = m.DeclaringType!.Module;
                var outs = new List<int>();
                foreach (var ins in Decode(il))
                {
                    if (ins.Op != OpCodes.Call && ins.Op != OpCodes.Callvirt && ins.Op != OpCodes.Newobj) continue;
                    var c = Resolve(mod, ins.Token, m);
                    if (c == null) continue;
                    var k = Key(c);
                    if (ReplacingWrites.Contains(k)) reachesWrite.Add(m.MetadataToken);
                    if (Reads.Contains(k)) reachesRead.Add(m.MetadataToken);
                    if (c.DeclaringType != null && inGroup.Contains(c.DeclaringType)) outs.Add(c.MetadataToken);
                }
                edges[m.MetadataToken] = outs;
            }

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var kv in edges)
                {
                    if (!reachesWrite.Contains(kv.Key) && kv.Value.Any(reachesWrite.Contains))
                    { reachesWrite.Add(kv.Key); changed = true; }
                    if (!reachesRead.Contains(kv.Key) && kv.Value.Any(reachesRead.Contains))
                    { reachesRead.Add(kv.Key); changed = true; }
                }
            }

            foreach (var m in members)
            {
                var body = BodyOf(m);
                var il = IlOf(m);
                if (body == null || il == null) continue;

                var clauses = body.ExceptionHandlingClauses
                    .Where(c => c.Flags == ExceptionHandlingClauseOptions.Clause
                             || c.Flags == ExceptionHandlingClauseOptions.Filter)
                    .ToList();
                if (clauses.Count == 0) continue;

                var instrs = Decode(il);
                if (instrs.Count == 0) continue;
                var byOffset = instrs.ToDictionary(x => x.Offset);
                var mod = m.DeclaringType!.Module;

                var readOffsets = new HashSet<int>();
                var writeOffsets = new Dictionary<int, string>();
                foreach (var ins in instrs)
                {
                    if (ins.Op != OpCodes.Call && ins.Op != OpCodes.Callvirt && ins.Op != OpCodes.Newobj) continue;
                    var c = Resolve(mod, ins.Token, m);
                    if (c == null) continue;
                    var k = Key(c);
                    bool sameGroup = c.DeclaringType != null && inGroup.Contains(c.DeclaringType);
                    if (Reads.Contains(k) || (sameGroup && reachesRead.Contains(c.MetadataToken)))
                        readOffsets.Add(ins.Offset);
                    if (ReplacingWrites.Contains(k) || (sameGroup && reachesWrite.Contains(c.MetadataToken)))
                        writeOffsets[ins.Offset] = k;
                }
                if (writeOffsets.Count == 0) continue;

                foreach (var clause in clauses)
                {
                    bool guardedRegionReads = readOffsets.Any(
                        o => o >= clause.TryOffset && o < clause.TryOffset + clause.TryLength);
                    if (!guardedRegionReads) continue;

                    foreach (var (writeOffset, call) in ReachableWrites(byOffset, writeOffsets, clause.HandlerOffset))
                    {
                        var owner = stateMachineOwner.TryGetValue(m.DeclaringType!, out var o) ? o : m;
                        findings.Add(new Finding(owner, Key(m), clause.HandlerOffset, writeOffset, call));
                    }
                }
            }
        }

        return findings.OrderBy(f => f.ToString(), StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Forward walk from a handler's first instruction: the handler body, and — because a C# catch
    /// that merely logs ends in <c>leave</c> — everything that <c>leave</c> lands on.
    /// </summary>
    private static IEnumerable<(int Offset, string Call)> ReachableWrites(
        Dictionary<int, Instr> byOffset, Dictionary<int, string> writes, int handlerOffset)
    {
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(handlerOffset);
        while (queue.Count > 0)
        {
            var off = queue.Dequeue();
            if (!seen.Add(off)) continue;
            if (!byOffset.TryGetValue(off, out var ins)) continue;

            if (writes.TryGetValue(off, out var call)) { yield return (off, call); continue; }

            var n = ins.Op.Name ?? string.Empty;
            if (n is "ret" or "throw" or "rethrow" or "endfinally" or "endfilter" or "endfault") continue;
            if (n is "br" or "br.s" or "leave" or "leave.s")
            { foreach (var t in ins.Targets) queue.Enqueue(t); continue; }
            foreach (var t in ins.Targets) queue.Enqueue(t);
            queue.Enqueue(ins.Next);
        }
    }

    /// <summary>
    /// Maps each compiler-generated state-machine type back to the <c>async</c> or iterator method that
    /// declares it. Without this an offending <c>async</c> method could never be declared: the IL is in
    /// <c>&lt;M&gt;d__N.MoveNext</c>, and an attribute written on <c>M</c> is not on <c>MoveNext</c>, so
    /// the exemption would never match and the census would be unfixable rather than merely red.
    /// </summary>
    private static Dictionary<Type, MethodBase> StateMachineOwners(Assembly asm)
    {
        var map = new Dictionary<Type, MethodBase>();
        foreach (var t in TypesOf(asm))
        {
            foreach (var m in DeclaredMethods(t))
            {
                try
                {
                    var a = m.GetCustomAttribute<AsyncStateMachineAttribute>();
                    if (a?.StateMachineType != null) map[a.StateMachineType] = m;
                    var i = m.GetCustomAttribute<IteratorStateMachineAttribute>();
                    if (i?.StateMachineType != null) map[i.StateMachineType] = m;
                }
                catch { }
            }
        }
        return map;
    }
}
