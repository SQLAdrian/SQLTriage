/* In the name of God, the Merciful, the Compassionate */

// -- alert-stamp-key-per-server (2026-09-15): the census behind the keying invariant -------------
//
// THE INVARIANT THIS CLASS DEFENDS. **Every read and every write of a freshness stamp must be keyed
// by the same tuple as the state it guards.** AlertEvaluationService keeps its live alert state in
// _activeStates, keyed per (alert, server). Anything that guards, times or judges one of those
// entries must therefore be keyed per (alert, server) too.
//
// WHY A CENSUS AND NOT A TEST OF THE ONE LINE. The defect was a SET completed as an INSTANCE: one
// dictionary out of six carried a coarser key than the state it guarded, and nothing in the suite
// could notice the odd one out. Two HAND-TYPED lists of the relevant sites were produced while this
// lane was being scoped and BOTH were wrong - one named three _activeStates sites where there are
// four, the other named a site belonging to a different collection entirely. A hand list cannot
// notice a member it never had. So this census enumerates from the COMPILED ARTIFACT and not from
// anybody's list: the fields by reflection, the field ACCESSES by decoding IL.
//
// WHAT EACH AXIS ACTUALLY PROVES - and what it does not. Read this before trusting a green.
//
//   AXIS 1 (reflection over declared fields) PROVES that every keyed collection on the class is
//   CLASSIFIED - that it carries [AlertKeyScope]. It goes red the moment a seventh collection is
//   added without one. It does NOT prove the declared scope is TRUE; a field declared PerAlert that
//   is really keyed per (alert, server) passes axis 1. That is what axis 3 is for, below.
//
//   AXIS 2 (IL decode) PROVES which methods touch which fields - exactly, from the bytes the
//   compiler emitted, not from where the source text sits. It catches the actual defect shape: a
//   method holding a per-(alert, server) state in one hand and a per-alert clock in the other, and
//   judging the first by the second. It does NOT do dataflow: it cannot prove WHICH key was passed
//   to a dictionary, only WHICH dictionaries a method reaches. A method that legitimately holds both
//   must declare [AlertKeyScopeMix] with a reason; declaring it makes it reviewed, not safe.
//
//   AXIS 4 (IL decode, CALL tokens) PROVES that every method which can fire an alert also reaches
//   RecordServerEvaluation, the one writer of the clock the reaper reads. Axes 1 and 3 are about the
//   collections that EXIST; axis 4 is about a PATH that forgets to write one, which neither of them
//   can see. It does NOT prove the stamp sits on every branch, nor that it is reached only once the
//   server has answered (a completed query, NULL included, since the owner's ruling of 2026-09-17) -
//   those are behavioural and belong to the H1, H2, R1 and I1 tests next door.
//
//   AXIS 3 is behavioural and lives next door, in
//   AlertStampKeyPerServerTests.Axis3_twoServersOfOneAlertProduceTwoKeysInEveryPerServerCollection
//   (that is its real name - check it still exists before trusting this sentence). It drives the
//   real engine against two servers and reads the keys back out of every collection THIS class
//   enumerates, through the shared KeyedCollections() below, which is what catches a scope that is
//   DECLARED wrongly. Axis 1 and axis 3 are a pair: one proves the declaration exists, the other
//   proves it is true. Neither is sufficient alone, and this comment is here so nobody deletes one
//   believing the other covers it.
//
// ⚠ THE "N method(s) walked" FIGURE IS A DIAGNOSTIC, NOT A FACT ABOUT THIS CLASS, and it is
// CONFIGURATION-DEPENDENT: measured 2026-09-16 on identical source, it is 190 in a Release build and
// 210 in a Debug build. WHY they differ is UNTESTED - the obvious candidate is that the two
// configurations emit different numbers of compiler-generated closure and state-machine members, but
// nobody checked, and a plausible cause is not a measured one. What IS measured is the pair of
// counts. Nothing below asserts on it - every haystack guard is "> 0" - so the census is not
// fragile. But do NOT quote the number without naming the configuration, and do not "correct" one
// figure to the other: the builder measured 190 in Release and the gate measured 210 in Debug, both
// honestly, and the only defect was that neither said which.
//
// THIS CLASS IS ON AN EXEMPTION LIST, and the paragraph above is the reason. Every test type that
// reads the product assembly's IL must either refuse a foreign build configuration or be listed in
// ProductCountGuardCensusTests.Exempt as pinning no number; this class is listed. The claim is
// checked, not trusted: ProductCountGuardCensusTests.The_exemption_list_holds_only_types_that_pin_no_number
// goes RED if a numeric assertion is added here. WHAT TO CHECK before adding one: whether the number
// is frozen against a particular build. If it is, the exemption's premise has expired.
//
// NOT A CHARACTER CENSUS, on purpose. EscalationParityCensusTests next door enumerates its firing
// paths with regexes over source text. That works until a line wraps or a comment quotes the
// pattern - this repo has measured both failure directions in one night. Nothing below reads source
// characters: reflection and IL see through formatting entirely.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class AlertStampKeyCensusTests
    {
        private readonly ITestOutputHelper _out;

        public AlertStampKeyCensusTests(ITestOutputHelper output) => _out = output;

        private static readonly Type Subject = typeof(AlertEvaluationService);

        /// <summary>
        /// The known-good collections. These are SPECIMEN CONTROLS, not the population: the census
        /// builds its population by reflection and these must turn up INSIDE it, correctly
        /// classified. If the enumerator stops finding them it has broken, and every "no findings"
        /// it reports afterwards would be the silence of an instrument that cannot see - which is
        /// the failure mode this repo has shipped more than once. They are named here rather than
        /// derived precisely so they are independent of the thing under test.
        /// </summary>
        private static readonly string[] KnownPerAlertServerCollections =
            { "_activeStates", "_lastNotified", "_escalatedEpisodes", "_hitTimes", "_evalFailures" };

        // ==========================================================================================
        // AXIS 1 - the population, and whether every member of it is classified
        // ==========================================================================================

        private sealed record KeyedField(FieldInfo Field, AlertKeyScopeKind? Scope)
        {
            public string Name => Field.Name;
            public bool Classified => Scope.HasValue;
        }

        /// <summary>Every string-keyed dictionary field declared on the subject, with whatever scope
        /// it declares. Reflection only - no list, no source text.</summary>
        private static List<KeyedField> Population()
        {
            var fields = Subject
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(IsStringKeyedDictionary)
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList();

            return fields
                .Select(f => new KeyedField(f, f.GetCustomAttribute<AlertKeyScopeAttribute>()?.Kind))
                .ToList();
        }

        /// <summary>
        /// The same reflection enumeration axis 1 uses, exposed so the behavioural corroboration in
        /// <see cref="AlertStampKeyPerServerTests"/> measures THE SAME POPULATION rather than a
        /// second list that could drift from this one. Axis 1 proves every member is classified;
        /// axis 3 proves the classification is true. They must agree on what the members are.
        /// </summary>
        internal static IReadOnlyList<(FieldInfo Field, AlertKeyScopeKind? Scope)> KeyedCollections() =>
            Population().Select(k => (k.Field, k.Scope)).ToList();

        private static bool IsStringKeyedDictionary(FieldInfo f)
        {
            var t = f.FieldType;
            if (!t.IsGenericType) return false;
            var def = t.GetGenericTypeDefinition();
            if (def != typeof(ConcurrentDictionary<,>) && def != typeof(Dictionary<,>)) return false;
            return t.GetGenericArguments()[0] == typeof(string);
        }

        private string Distribution(IReadOnlyList<KeyedField> pop)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"THE WHOLE POPULATION - {pop.Count} string-keyed collection(s) on {Subject.Name}:");
            foreach (var k in pop)
            {
                var scope = k.Classified ? k.Scope!.Value.ToString() : "*** UNCLASSIFIED ***";
                sb.AppendLine($"   {k.Name,-28} {scope,-22} {Pretty(k.Field.FieldType)}");
            }
            var byScope = pop.GroupBy(k => k.Classified ? k.Scope!.Value.ToString() : "UNCLASSIFIED")
                             .OrderBy(g => g.Key, StringComparer.Ordinal);
            sb.AppendLine("   -- counts --");
            foreach (var g in byScope) sb.AppendLine($"   {g.Key,-22} {g.Count()}");
            return sb.ToString();
        }

        private static string Pretty(Type t) =>
            !t.IsGenericType ? t.Name
            : t.Name.Split('`')[0] + "<" + string.Join(", ", t.GetGenericArguments().Select(Pretty)) + ">";

        [Fact]
        public void Axis1_everyKeyedCollectionDeclaresItsScope_andTheKnownGoodOnesAreStillVisible()
        {
            var pop = Population();
            var report = Distribution(pop);
            _out.WriteLine(report);

            // HAYSTACK FIRST. Measure the needle only once the haystack is proved non-empty: an
            // enumerator that returns nothing would otherwise report a clean census forever.
            Assert.True(pop.Count > 0,
                "The census found NO string-keyed collections on " + Subject.Name + " at all. It has not "
                + "proved the class is clean - it has proved the enumerator is blind. Check that "
                + "IsStringKeyedDictionary still matches the collection types the class actually uses "
                + "(it looks for ConcurrentDictionary<string,*> and Dictionary<string,*> instance fields).\n"
                + report);

            // SPECIMEN CONTROL. The known-good collections must appear in the population, and must
            // still be classified per (alert, server). If they have vanished, the finding is about
            // the instrument, not the code.
            var byName = pop.ToDictionary(k => k.Name, StringComparer.Ordinal);
            var missing = KnownPerAlertServerCollections.Where(n => !byName.ContainsKey(n)).ToList();
            Assert.True(missing.Count == 0,
                "SPECIMEN CONTROL FAILED: the census can no longer see " + string.Join(", ", missing)
                + ". Every other result from this census is now suspect, because an enumerator that "
                + "cannot find a collection it is pointed at cannot be trusted to find one it is not. "
                + "Check whether those fields were renamed or removed, and whether the census's own "
                + "control list needs to follow them - do NOT simply delete the name from the list.\n"
                + report);

            var misScoped = KnownPerAlertServerCollections
                .Where(n => byName[n].Scope != AlertKeyScopeKind.PerAlertServer)
                .ToList();
            Assert.True(misScoped.Count == 0,
                "SPECIMEN CONTROL FAILED: " + string.Join(", ", misScoped) + " no longer declare "
                + "PerAlertServer. These collections are keyed by (alert, server) as a matter of "
                + "record. Check what changed about their keying, and whether the change was "
                + "intended.\n" + report);

            // THE NEEDLE.
            var unclassified = pop.Where(k => !k.Classified).Select(k => k.Name).ToList();
            Assert.True(unclassified.Count == 0,
                "UNCLASSIFIED COLLECTION(S): " + string.Join(", ", unclassified) + ".\n\n"
                + "Every string-keyed collection on " + Subject.Name + " must carry [AlertKeyScope], "
                + "because the freshness defect this census exists for was one collection keyed more "
                + "coarsely than the state it guarded, and nothing could tell.\n\n"
                + "WHAT TO CHECK for each one named above: what tuple do its keys actually identify, "
                + "and what does it guard? If it guards, times or judges an _activeStates entry, its "
                + "keys must come from AlertEvaluationService.StateKey. Decide that first; the "
                + "attribute records the decision, it does not make it.\n" + report);
        }

        // ==========================================================================================
        // AXIS 2 - which methods touch which collections, decoded from IL
        // ==========================================================================================

        private static readonly Dictionary<short, OpCode> OpcodeByValue =
            typeof(OpCodes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(OpCode))
                .Select(f => (OpCode)f.GetValue(null)!)
                .ToDictionary(o => o.Value);

        private sealed class MethodScan
        {
            public MethodBase Method = null!;
            public string Display = "";
            public HashSet<string> Touches = new(StringComparer.Ordinal);
            public HashSet<string> Calls = new(StringComparer.Ordinal);
            public string? Unreadable;   // non-null => the scan could not read this method's FIELDS
            // Call-token resolution keeps its OWN could-not-read channel. A method token this
            // decoder cannot resolve says nothing about the field scan, and folding the two would
            // let a call failure turn axis 2 red for a reason axis 2 is not about.
            public string? CallsUnreadable;
            public bool Bodyless;        // abstract / extern: no IL exists, which is not the same thing
        }

        /// <summary>
        /// Walks the IL of every method on the subject AND its nested compiler-generated types
        /// (async state machines and lambda closures hold the real field accesses, not the source
        /// method), and records which population fields each one touches.
        ///
        /// <para>Instruction boundaries are walked properly, using the operand sizes read off
        /// <see cref="OpCodes"/> itself - not by scanning for opcode bytes, which would happily read
        /// an operand byte as an instruction.</para>
        /// </summary>
        private static List<MethodScan> ScanIl(IReadOnlyCollection<string> populationNames)
        {
            var results = new List<MethodScan>();
            var types = new List<Type> { Subject };
            types.AddRange(Subject.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));

            foreach (var type in types)
            {
                var members = type
                    .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Cast<MethodBase>()
                    .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));

                foreach (var m in members)
                {
                    var scan = new MethodScan { Method = m, Display = type.Name + "." + m.Name };
                    try
                    {
                        var body = m.GetMethodBody();
                        if (body == null)
                        {
                            // No IL EXISTS for an abstract, extern or P/Invoke method - the CLR
                            // guarantees it. That is "nothing to read", which is decidable, and not
                            // the same as "could not read". Anything else with a null body is a
                            // genuine gap and is reported as unreadable below.
                            var noIlByDesign = m.IsAbstract
                                || (m.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0
                                || (m.Attributes & MethodAttributes.PinvokeImpl) != 0;
                            if (noIlByDesign) scan.Bodyless = true;
                            else scan.Unreadable = "GetMethodBody() returned null on a method that should have IL";
                            results.Add(scan);
                            continue;
                        }

                        var il = body.GetILAsByteArray();
                        if (il == null)
                        {
                            scan.Unreadable = "GetILAsByteArray() returned null";
                            results.Add(scan);
                            continue;
                        }

                        var typeArgs = type.IsGenericType ? type.GetGenericArguments() : null;
                        var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;

                        var i = 0;
                        while (i < il.Length)
                        {
                            short value = il[i];
                            i++;
                            if (value == 0xFE)
                            {
                                if (i >= il.Length) { scan.Unreadable = "IL ended inside a two-byte opcode"; break; }
                                value = (short)(0xFE00 | il[i]);
                                i++;
                            }

                            if (!OpcodeByValue.TryGetValue(value, out var op))
                            {
                                scan.Unreadable = $"unknown opcode 0x{value:X} at offset {i - 1}";
                                break;
                            }

                            if (op.OperandType == OperandType.InlineField)
                            {
                                if (i + 4 > il.Length) { scan.Unreadable = "IL ended inside a field token"; break; }
                                var token = BitConverter.ToInt32(il, i);
                                try
                                {
                                    var fi = type.Module.ResolveField(token, typeArgs, methodArgs);
                                    if (fi != null && fi.DeclaringType == Subject && populationNames.Contains(fi.Name))
                                        scan.Touches.Add(fi.Name);
                                }
                                catch (Exception ex)
                                {
                                    scan.Unreadable = "could not resolve a field token: " + ex.GetType().Name;
                                    break;
                                }
                            }

                            if (op.OperandType == OperandType.InlineMethod)
                            {
                                if (i + 4 > il.Length) { scan.Unreadable = "IL ended inside a method token"; break; }
                                var token = BitConverter.ToInt32(il, i);
                                try
                                {
                                    var mi = type.Module.ResolveMethod(token, typeArgs, methodArgs);
                                    if (mi != null && mi.DeclaringType == Subject)
                                        scan.Calls.Add(mi.Name);
                                }
                                catch (Exception ex)
                                {
                                    // Recorded, NOT breaking: the field walk below is a different
                                    // question and must finish. An unresolved call token means this
                                    // scan cannot RULE OUT a call, which axis 4 reports as a gap.
                                    scan.CallsUnreadable ??= "could not resolve a method token: "
                                        + ex.GetType().Name;
                                }
                            }

                            var skip = OperandSize(op, il, i);
                            if (skip < 0) { scan.Unreadable = "unhandled operand type " + op.OperandType; break; }
                            i += skip;
                        }
                    }
                    catch (Exception ex)
                    {
                        scan.Unreadable = ex.GetType().Name + ": " + ex.Message;
                    }

                    results.Add(scan);
                }
            }

            return results;
        }

        private static int OperandSize(OpCode op, byte[] il, int operandStart) => op.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch =>
                operandStart + 4 > il.Length ? -1 : 4 + 4 * BitConverter.ToInt32(il, operandStart),
            _ => -1,
        };

        /// <summary>
        /// Maps a compiler-generated method back to the source method that owns it, so a declared
        /// scope mix can be read off the method a human actually wrote. Async and iterator state
        /// machines are mapped EXACTLY, off the attribute the compiler emits. Lambdas and closures
        /// have no such attribute, so they are mapped by the owner name the compiler embeds in the
        /// member name ("&lt;EvaluateAllAsync&gt;b__6_0"). That second route is a NAME convention,
        /// not a guarantee - which is why an owner that cannot be resolved is reported as a finding
        /// rather than waved through.
        /// </summary>
        private static MethodBase? OwnerOf(MethodBase m, IReadOnlyDictionary<Type, MethodBase> stateMachineOwners)
        {
            if (m.DeclaringType == Subject && !IsCompilerGenerated(m)) return m;

            if (m.DeclaringType != null && stateMachineOwners.TryGetValue(m.DeclaringType, out var owner))
                return owner;

            var name = ExtractOwnerName(m.Name) ?? (m.DeclaringType != null ? ExtractOwnerName(m.DeclaringType.Name) : null);
            if (name == null) return null;

            var candidates = Subject
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(x => x.Name == name)
                .ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        /// <summary>
        /// Maps each compiler-emitted state-machine type back to the source method that owns it,
        /// read off the attribute the compiler emits. Shared by axis 2 and axis 4 so the two cannot
        /// disagree about which source method a MoveNext belongs to.
        /// </summary>
        private static Dictionary<Type, MethodBase> StateMachineOwners()
        {
            var owners = new Dictionary<Type, MethodBase>();
            foreach (var m in Subject.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var sm = m.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                         ?? m.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
                if (sm != null) owners[sm] = m;
            }
            return owners;
        }

        private static bool IsCompilerGenerated(MethodBase m) =>
            m.Name.StartsWith("<", StringComparison.Ordinal)
            || (m.DeclaringType?.GetCustomAttribute<CompilerGeneratedAttribute>() != null);

        /// <summary>Peels "&lt;&lt;Outer&gt;b__6_0&gt;d__7" down to "Outer".</summary>
        private static string? ExtractOwnerName(string name)
        {
            while (true)
            {
                var open = name.IndexOf('<');
                if (open < 0) return null;
                var close = MatchingAngle(name, open);
                if (close < 0) return null;
                var inner = name.Substring(open + 1, close - open - 1);
                if (inner.Length == 0) return null;
                if (inner[0] != '<') return inner;
                name = inner;
            }
        }

        private static int MatchingAngle(string s, int open)
        {
            var depth = 0;
            for (var i = open; i < s.Length; i++)
            {
                if (s[i] == '<') depth++;
                else if (s[i] == '>' && --depth == 0) return i;
            }
            return -1;
        }

        [Fact]
        public void Axis2_noMethodHoldsAPerServerStateAndAPerAlertClockWithoutSayingWhy()
        {
            var pop = Population();
            var names = pop.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var scopeOf = pop.Where(p => p.Classified).ToDictionary(p => p.Name, p => p.Scope!.Value, StringComparer.Ordinal);

            var scans = ScanIl(names);

            var stateMachineOwners = StateMachineOwners();

            var touching = scans.Where(s => s.Touches.Count > 0).ToList();
            // Constructors are EXCLUDED and the exclusion is printed, not silent: field initialisers
            // are emitted into .ctor, so every constructor touches every field. Initialising a
            // collection is not judging one by another, which is the only thing this axis is about.
            var constructors = touching.Where(s => s.Method is ConstructorInfo).ToList();
            var judged = touching.Where(s => s.Method is not ConstructorInfo).ToList();

            var sb = new StringBuilder();
            // Diagnostic only, and configuration-dependent (Release 190 / Debug 210 on 2026-09-16,
            // same source; the CAUSE of the gap is untested). Nothing asserts on it - see the header
            // before quoting this number anywhere.
            sb.AppendLine($"IL SCAN: {scans.Count} method(s) walked on {Subject.Name} and its nested types.");
            sb.AppendLine($"   touching a population field : {touching.Count}");
            sb.AppendLine($"   excluded as constructors    : {constructors.Count} ({string.Join(", ", constructors.Select(c => c.Display))})");
            sb.AppendLine($"   bodyless by design          : {scans.Count(s => s.Bodyless)}");
            sb.AppendLine($"   UNREADABLE                  : {scans.Count(s => s.Unreadable != null)}");
            sb.AppendLine("   -- the whole distribution, every method that touches a keyed collection --");
            foreach (var s in judged.OrderBy(s => s.Display, StringComparer.Ordinal))
            {
                var parts = s.Touches.OrderBy(x => x, StringComparer.Ordinal)
                    .Select(n => n + "[" + (scopeOf.TryGetValue(n, out var k) ? k.ToString() : "UNCLASSIFIED") + "]");
                sb.AppendLine($"   {s.Display,-58} {string.Join(" ", parts)}");
            }
            var report = sb.ToString();
            _out.WriteLine(report);

            // HAYSTACK FIRST, in both directions: methods were walked at all, AND some of them
            // reached a collection. A scan that walked 400 methods and found no field access is a
            // broken decoder reporting a clean class.
            Assert.True(scans.Count > 0,
                "The IL scan walked NO methods. That is a broken enumerator, not a clean class. Check "
                + "the BindingFlags in ScanIl.\n" + report);
            Assert.True(judged.Count > 0,
                "The IL scan walked " + scans.Count + " methods and found NOT ONE touching a keyed "
                + "collection. " + Subject.Name + " certainly does touch them, so the decoder or the "
                + "field-token resolution has broken and every clean result it reports is meaningless. "
                + "Check OperandSize and the ResolveField call.\n" + report);

            // COULD-NOT-READ is its own answer and never shares one with "found nothing".
            var unreadable = scans.Where(s => s.Unreadable != null).ToList();
            Assert.True(unreadable.Count == 0,
                "THE CENSUS COULD NOT READ " + unreadable.Count + " METHOD(S), so its silence about them "
                + "is not evidence:\n"
                + string.Join("\n", unreadable.Select(u => "   " + u.Display + " - " + u.Unreadable))
                + "\n\nWHAT TO CHECK: whether the IL decoder needs to handle a construct this class has "
                + "started using. Until each one named above is readable, this census does not cover it.\n"
                + report);

            // THE NEEDLE: a method holding both scopes at once, without saying why.
            var findings = new List<string>();
            foreach (var s in judged)
            {
                var scopes = s.Touches.Where(scopeOf.ContainsKey).Select(n => scopeOf[n]).Distinct().ToList();
                if (!scopes.Contains(AlertKeyScopeKind.PerAlertServer) || !scopes.Contains(AlertKeyScopeKind.PerAlert))
                    continue;

                var owner = OwnerOf(s.Method, stateMachineOwners);
                if (owner == null)
                {
                    findings.Add(s.Display + " - holds both scopes AND the census could not work out which "
                        + "source method owns it, so it cannot tell whether the mix was declared");
                    continue;
                }
                if (owner.GetCustomAttribute<AlertKeyScopeMixAttribute>() == null)
                    findings.Add(s.Display + " (owner: " + owner.Name + ") - touches "
                        + string.Join(", ", s.Touches.OrderBy(x => x, StringComparer.Ordinal)));
            }

            Assert.True(findings.Count == 0,
                "UNDECLARED SCOPE MIX:\n" + string.Join("\n", findings.Select(f => "   " + f)) + "\n\n"
                + "Each method above reaches BOTH a per-(alert, server) collection and a per-alert one. "
                + "That is the exact shape of the defect this census exists for: a per-server state "
                + "judged by an alert-wide clock, so a clean run on one server silently cleared an "
                + "alert on another.\n\n"
                + "WHAT TO CHECK: does the method use the per-alert value to decide anything about the "
                + "per-server state - its freshness, its removal, its cooldown? If it does, that is the "
                + "defect and the per-alert clock is the wrong input. If the two are genuinely "
                + "independent, say so on the source method with [AlertKeyScopeMix(\"reason\")]. The "
                + "census cannot tell these apart - it can only make sure a person looked.\n" + report);
        }

        // ==========================================================================================
        // AXIS 4 - does every firing path actually WRITE the clock the reaper reads?
        // ==========================================================================================

        /// <summary>
        /// The one writer of the measurement clock <c>ShouldAutoResolveAsCleared</c> is judged by.
        /// A firing path that never reaches it leaves its alert with no per-server stamp at all.
        /// </summary>
        private const string MeasurementStamp = "RecordServerEvaluation";

        /// <summary>
        /// How a firing path is recognised, from the compiled artifact: it reaches one of the two
        /// shared callees only a firing cycle has reason to call.
        ///
        /// <para>Neither signal method calls the other or itself, checked at the time of writing, so
        /// neither enters the firing set merely by being a signal. That is a fact about today's code
        /// and NOTHING BELOW RE-CHECKS IT - the non-vacuity assertion re-checks something else, that
        /// both KNOWN paths are still found. It is safe to leave unguarded only because the failure
        /// is loud in the right direction: a signal method that did enter the set would not call the
        /// stamp either, so it would fail THE NEEDLE by name rather than pass quietly.</para>
        /// </summary>
        private static readonly string[] FiringPathSignals =
            { "LogAlertFired", "ApplyEscalationForFiringCycle" };

        /// <summary>
        /// THE GUARD THE COMMENT ON <c>RecordServerEvaluation</c> PROMISES. Axis 1 notices a
        /// collection added without a scope; axis 3 notices a scope declared wrongly. NEITHER
        /// notices a third FIRING PATH that fires an alert and never stamps the measurement clock -
        /// and that omission is silent by construction, because its consequence is an alert that is
        /// never auto-resolved, which reads as caution rather than as a bug.
        ///
        /// <para><b>Why here and not beside the escalation census.</b>
        /// <c>EscalationParityCensusTests.EveryFiringPathEvaluatesEscalation</c> asks the same shape
        /// of question and is the honest precedent for it, but it reads SOURCE CHARACTERS through
        /// regexes. This repository has measured both of that instrument's failure directions in one
        /// night - a wrapped line hiding a real offender, and prose inside a string counting as one -
        /// so a character census is a tripwire, not a guarantee. This axis reuses the IL walk the
        /// rest of this class already runs: it resolves CALL tokens exactly as axis 2 resolves field
        /// tokens, and so sees through formatting, wrapping, comments and aliasing entirely.</para>
        ///
        /// <para><b>Calls are aggregated onto the SOURCE method that owns them.</b> Both firing paths
        /// are async, so their real IL lives in a compiler-emitted state machine and not in the stub
        /// left behind under the written name. Reading the stub alone would find no calls at all and
        /// pass vacuously.</para>
        ///
        /// <para><b>What this axis does NOT prove.</b> That the stamp is reached on every BRANCH, or
        /// with the right arguments, or only once the server has answered. It proves the call exists
        /// in the path. Those questions are behavioural and belong to the H1, H2, R1 and I1 tests in
        /// <c>AlertStampKeyPerServerTests</c>, which drive the real engine. This axis is what stops a
        /// path shipping that none of those tests was ever written for.</para>
        /// </summary>
        [Fact]
        public void Axis4_everyFiringPathWritesTheMeasurementClock()
        {
            var scans = ScanIl(Population().Select(p => p.Name).ToHashSet(StringComparer.Ordinal));
            var owners = StateMachineOwners();

            var callsByOwner = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var unattributed = new List<string>();
            var unreadable = new List<string>();

            foreach (var scan in scans)
            {
                if (scan.CallsUnreadable != null)
                    unreadable.Add(scan.Display + " - " + scan.CallsUnreadable);

                if (scan.Calls.Count == 0) continue;

                var owner = OwnerOf(scan.Method, owners);
                if (owner == null)
                {
                    // Only a finding when it matters: an unattributable scan that reaches a firing
                    // signal is a hole in this census; one that reaches nothing relevant is not.
                    if (scan.Calls.Any(c => FiringPathSignals.Contains(c, StringComparer.Ordinal)))
                        unattributed.Add(scan.Display);
                    continue;
                }

                if (!callsByOwner.TryGetValue(owner.Name, out var set))
                    callsByOwner[owner.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in scan.Calls) set.Add(c);
            }

            var firing = callsByOwner
                .Where(kv => kv.Value.Any(c => FiringPathSignals.Contains(c, StringComparer.Ordinal)))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"FIRING-PATH CENSUS (IL call tokens): {scans.Count} method(s) walked on {Subject.Name}.");
            sb.AppendLine($"   methods reaching any call on the subject : {callsByOwner.Count}");
            sb.AppendLine($"   recognised as firing paths               : {firing.Count}");
            sb.AppendLine($"   CALL TOKENS UNREADABLE                   : {unreadable.Count}");
            sb.AppendLine("   -- the whole firing distribution, stamp or no stamp --");
            foreach (var entry in firing)
            {
                var signals = string.Join(", ", entry.Value
                    .Where(c => FiringPathSignals.Contains(c, StringComparer.Ordinal))
                    .OrderBy(x => x, StringComparer.Ordinal));
                var verdict = entry.Value.Contains(MeasurementStamp) ? "STAMPS" : "*** NO STAMP ***";
                sb.AppendLine($"   {entry.Key,-42} {verdict,-16} signals: {signals}");
            }
            var report = sb.ToString();
            // Printed on PASS as well as on failure: a census whose distribution is only visible
            // when it fails cannot be audited on the day it matters.
            _out.WriteLine(report);

            // COULD-NOT-READ never shares an answer with FOUND-NOTHING.
            Assert.True(unreadable.Count == 0,
                "THE CENSUS COULD NOT RESOLVE A CALL TOKEN IN " + unreadable.Count + " METHOD(S), so it "
                + "cannot rule out that one of them reaches a firing-path signal:\n"
                + string.Join("\n", unreadable.Select(u => "   " + u))
                + "\n\nWHAT TO CHECK: whether the IL decoder needs to handle a construct this class has "
                + "started using - a generic instantiation or a function pointer, most likely.\n" + report);

            Assert.True(unattributed.Count == 0,
                "A compiler-generated method reaches a firing-path signal and this census could not "
                + "work out which source method owns it, so it cannot say whether that path stamps:\n   "
                + string.Join("\n   ", unattributed)
                + "\n\nWHAT TO CHECK: OwnerOf, and whether the compiler has emitted a member-name shape "
                + "it does not peel.\n" + report);

            // NON-VACUITY, and not a count pinned off a pattern: these two methods exist, they are
            // the two firing paths, and this decoder must be able to see BOTH. If it cannot, the
            // clean result below is the silence of a broken instrument.
            var found = firing.Select(f => f.Key).ToList();
            foreach (var known in new[] { "EvaluateSpecialAlertAsync", "ApplyObservedValueAsync" })
                Assert.True(found.Contains(known, StringComparer.Ordinal),
                    $"the census did not recognise the known firing path {known}. It found: "
                    + (found.Count == 0 ? "nothing at all" : string.Join(", ", found))
                    + ". That is a decoder failure, and a decoder failure here is a SILENT HOLE, not "
                    + "a pass.\n" + report);

            // THE NEEDLE.
            var missing = firing.Where(f => !f.Value.Contains(MeasurementStamp))
                .Select(f => f.Key).ToList();

            Assert.True(missing.Count == 0,
                "THESE METHODS CAN FIRE AN ALERT AND NEVER WRITE THE MEASUREMENT CLOCK:\n   "
                + string.Join("\n   ", missing)
                + "\n\nEvery firing path must call " + MeasurementStamp + "(alert.Id, serverName, "
                + "DateTime.UtcNow) at the point the server has ANSWERED - on the standard path a query "
                + "that completed, a NULL result included (owner's ruling 2026-09-17) - and never on an "
                + "attempt, a throw, a timeout or a breaker-open skip. "
                + "A path that fires an alert and never stamps "
                + "leaves _lastServerEvaluation with no entry for that (alert, server), so "
                + "ShouldAutoResolveAsCleared never auto-resolves it and the alert stays on the wall "
                + "indefinitely. That fails SAFE, which is exactly why nobody would notice it.\n\n"
                + "WHAT TO CHECK: where in the new path the server's answer is actually in hand, and put "
                + "the stamp there. Do not add the method to an exclusion list, and do not move the stamp to the "
                + "top of the path to make this green - that reintroduces the H2 half of the defect "
                + "this lane closed.\n" + report);
        }
    }
}
