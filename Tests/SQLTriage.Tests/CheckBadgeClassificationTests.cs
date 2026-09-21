/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT: <b>a check that did not assert anything about this server must never render as a
/// green pass.</b>
///
/// <para><b>WHY THIS EXISTS.</b> A result can carry <c>Passed == true</c> and still have asserted
/// nothing — <see cref="CheckClassification"/> names three such tiers, and its own summary says so:
/// "Any surface showing Passed/Failed counts or top-findings/critical lists must filter to this subset
/// first, or a SKIP/INFO/WARN result inflates a pass count or shows up as a 'finding' it never was."
/// The badge at <c>Pages/Checks.razor</c> is such a surface, and it is the one a client looks at.</para>
///
/// <para><b>THIS HAS ALREADY COST REAL DAMAGE ONCE.</b> <c>CheckClassification.cs:50-58</c> records it:
/// before the 2026-07-16 gate-B1 fix, "53 of 55 Verdict-SKIP corpus results, incl. 4 Critical/24 High
/// severity, escaped into scored Passed and rendered a green PASS badge, e.g. SQLT-CORE-00580 'No AGs
/// configured.'" That fix repaired <see cref="CheckClassification.IsSkip"/>. <b>It never reached the
/// badge, because the badge does not call IsSkip.</b> A fix that repairs a predicate does not repair
/// the surfaces that failed to call it.</para>
///
/// <para><b>WHY THE BADGE WAS WRONG AND THE STAT CARD WAS RIGHT, in the same file, by the same hand.</b>
/// The outcome filters and the Passed stat card AND their raw-Passed allowlist reasons all use
/// <c>IsScorable</c> (see <c>raw-passed-allowlist.tsv</c> rows for <c>"passed" =&gt; rows.Where(...)</c>
/// and <c>@StatCard("Passed", ...)</c>). Only the badge used <c>IsWarn</c> alone. The comment above it
/// and its own two allowlist rows both explained themselves purely in terms of WARN — "Ruling #4:
/// CheckClassification.IsWarn(r) is tested earlier in the same conditional expression, so the Passed arm
/// is only reached by a non-WARN result." True, and silent about SKIP and INFO. <b>A justification that
/// is correct about the case its author had in mind, and mute on the ones they did not enumerate, is how
/// a guarded line stays broken.</b></para>
///
/// <para>The rendering logic lives in <see cref="CheckClassification"/> rather than inline in the
/// component so that it is (a) testable at all and (b) has ONE definition. Testing it here tests the
/// thing the page calls, not a copy of it — <see cref="TheChecksPageCallsTheSharedBadgeHelper"/>
/// pins that the component has not drifted back to its own inline copy.</para>
/// </summary>
public sealed class CheckBadgeClassificationTests
{
    private static CheckResult Result(
        bool passed, string? verdict = null, string? severity = null,
        string? errorMessage = null, bool corrupted = false, string message = "")
        => new()
        {
            CheckId = "SQLT-TEST-00001",
            Passed = passed,
            Verdict = verdict,
            Severity = severity ?? "Medium",
            ErrorMessage = errorMessage,
            IsCorrupted = corrupted,
            Message = message,
        };

    // ── The defect this class was written for ────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ THE ONE THAT MATTERS. A verdict-contract check that SKIPPED carries Passed == true — that is
    /// not a quirk, it is how the executor represents "not applicable", and it is exactly the shape
    /// that put 53 green badges on skipped checks in July.
    /// </summary>
    [Fact]
    public void A_skipped_check_never_renders_as_a_green_pass()
    {
        var skipped = Result(passed: true, verdict: "SKIP");

        CheckClassification.IsSkip(skipped).Should().BeTrue("the fixture must actually be a SKIP, "
            + "or this test proves nothing about skips");

        CheckClassification.BadgeLabel(skipped).Should().NotBe("✓ pass",
            "a check that asserted nothing about this server must not tell a client it passed");
        CheckClassification.BadgeColorVar(skipped).Should().NotBe("var(--green)",
            "and it must not be coloured as one either — the colour is read before the words");
    }

    /// <summary>The other two non-scorable tiers, which the badge also fell through on.</summary>
    [Theory]
    [InlineData("SKIP", null, null)]
    [InlineData("INFO", null, null)]
    [InlineData(null, "INFO", null)]          // INFO declared by Severity, not Verdict
    [InlineData(null, null, "permission denied")]  // an execution error is a skip by IsSkip's own rule
    public void No_non_scorable_result_renders_as_a_green_pass(string? verdict, string? severity, string? error)
    {
        var r = Result(passed: true, verdict: verdict, severity: severity, errorMessage: error);

        CheckClassification.IsScorable(r).Should().BeFalse(
            "the fixture must be non-scorable, or the assertion below is vacuous");
        CheckClassification.BadgeLabel(r).Should().NotBe("✓ pass");
        CheckClassification.BadgeColorVar(r).Should().NotBe("var(--green)");
    }

    /// <summary>A skipped check reads as skipped, not merely as "not a pass".</summary>
    [Fact]
    public void A_skipped_check_says_so()
    {
        CheckClassification.BadgeLabel(Result(passed: true, verdict: "SKIP"))
            .Should().Contain("skip", "\"not assessed\" is the honest reading and the reader needs the word");
    }

    // ── The other direction. Without these the fix is unfalsifiable: a badge that renders
    //    "✗ fail" for everything would pass every assertion above. ─────────────────────────────────

    [Fact]
    public void A_real_pass_still_renders_as_a_green_pass()
    {
        var pass = Result(passed: true);
        CheckClassification.IsScorable(pass).Should().BeTrue();
        CheckClassification.BadgeLabel(pass).Should().Be("✓ pass");
        CheckClassification.BadgeColorVar(pass).Should().Be("var(--green)");
    }

    [Fact]
    public void A_real_failure_still_renders_as_a_red_fail()
    {
        var fail = Result(passed: false);
        CheckClassification.BadgeLabel(fail).Should().Be("✗ fail");
        CheckClassification.BadgeColorVar(fail).Should().Be("var(--red)");
    }

    /// <summary>Ruling #4's original case, unchanged by this lane. A regression here is a regression.</summary>
    [Fact]
    public void A_warn_still_renders_as_partial()
    {
        var warn = Result(passed: true, verdict: "WARN");
        CheckClassification.BadgeLabel(warn).Should().Be("⚠ partial");
        CheckClassification.BadgeColorVar(warn).Should().Be("var(--orange)");
    }

    /// <summary>Corrupted stays first in the chain — it outranks every other tier.</summary>
    [Fact]
    public void A_corrupted_result_still_renders_as_corrupted()
    {
        var corrupt = Result(passed: true, verdict: "SKIP", corrupted: true);
        CheckClassification.BadgeLabel(corrupt).Should().Be("corrupted");
        CheckClassification.BadgeColorVar(corrupt).Should().Be("var(--purple)");
    }

    // ── The pin that stops this coming back ──────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ THE TESTS ABOVE PROVE THE HELPER IS RIGHT. THEY DO NOT PROVE THE PAGE CALLS IT. That gap is
    /// exactly how this defect survived three fixes: the 2026-07-16 repair of <c>IsSkip</c> was correct
    /// and the badge simply never called it. So this asserts the CALL.
    ///
    /// <para><b>IT READS COMPILED IL, NOT SOURCE TEXT, and that choice was forced by a measurement.</b>
    /// The first version of this test grepped <c>Pages/Checks.razor</c> for the string
    /// <c>"CheckClassification.BadgeLabel(r)"</c> and for the absence of an inline ternary. A peer
    /// session measured the identical shape failing on 2026-09-15: a census
    /// grepping <c>DdlJournal.RecordAttempt</c> missed every call site that injected the journal under a
    /// different local name (<c>Journal.</c>, <c>_journal?.</c>). The receiver's SPELLING is not the
    /// call. Worse, the absence half of that check failed toward CLEAN — reformat the ternary, add a line
    /// break, and it matches nothing and passes green, which is the direction that lets a defect back in
    /// silently. This repo has already PROVED character censuses failing in both directions (2026-09-11).</para>
    ///
    /// <para>An IL check is immune to all of it: receiver naming, <c>@using static</c>, whitespace, line
    /// breaks. And ONE positive assertion covers both directions — if anyone reverts to an inline
    /// ternary, the call disappears and this goes red, so no separate "absence" check is needed.
    /// A Razor component compiles to an ordinary class, so it is just another type in the assembly.</para>
    ///
    /// <para><b>What this still does not prove:</b> that the value returned is what reaches the DOM. That
    /// would need a rendered component, and there is no bUnit in this project. Tagged rather than hidden.</para>
    ///
    /// <para><b>⚠ IT READS PRODUCT IL AND PINS NO NUMBER, AND THAT IS WHY IT NEEDS NO CONFIGURATION
    /// GUARD.</b> A count taken off product IL is specific to the build configuration it was measured
    /// in — PROVED 2026-09-16 at <c>7b997b4</c>: the S-1 census reads 120 command sites in Release and
    /// 117 in Debug at the identical commit, because Roslyn merges hoisted async state-machine fields
    /// when optimisations are ON. This test asserts the PRESENCE of two resolved call targets, with a
    /// non-empty haystack asserted first, so there is no frozen magnitude for a configuration to
    /// invalidate and it passes on both axes. That exemption is RECORDED AND CHECKED, not assumed:
    /// this type is named on <c>ProductCountGuardCensusTests.Exempt</c> with that reason, and
    /// <see cref="ProductCountGuardCensusTests.The_exemption_list_holds_only_types_that_pin_no_number"/>
    /// goes RED the day anyone adds a numeric assertion here. If you need a count in this class, call
    /// <see cref="ProductAssemblyBuildConfiguration.RequireTheBuildConfigurationTheseNumbersWereFrozenIn"/>
    /// first and delete the exemption.</para>
    /// </summary>
    [Fact]
    public void TheChecksPageCallsTheSharedBadgeHelper()
    {
        var product = typeof(CheckClassification).Assembly;
        var page = SessionSafetyAnalyzer.TypesOf(product)
            .FirstOrDefault(t => t != null && t.Name == "Checks" && t.Namespace == "SQLTriage.Pages");

        // HAYSTACK BEFORE NEEDLE. If the component type cannot be located, every assertion below is
        // vacuous and would pass a broken build — the shape this repo keeps measuring.
        page.Should().NotBeNull("the compiled Checks component must be found before its IL means anything; "
            + "if this fails the component was renamed or moved, not that the badge is fine");

        var called = CalledMemberNames(page!);
        called.Should().NotBeEmpty("the component's IL must decode, or the absence of a call proves nothing");

        called.Should().Contain("SQLTriage.Data.Services.CheckClassification.BadgeLabel",
            "the Checks page must render the badge through the ONE definition. If this went red because "
            + "someone wrote the ternary inline again, that is the defect: three tiers ride Passed=true "
            + "and an inline arm has answered only WARN twice now");
        called.Should().Contain("SQLTriage.Data.Services.CheckClassification.BadgeColorVar",
            "and the colour must come from the same definition as the words — a green cell with honest "
            + "words still reads as a pass");
    }

    /// <summary>
    /// Every method the given type (and its compiler-generated nested types) CALLS, as
    /// <c>Namespace.Type.Member</c>. Self-contained on purpose: the house convention is that each
    /// analyser keeps its own copy of the IL primitives, so a change made for one invariant cannot
    /// quietly alter another's reading — <c>SessionSafetyAnalyzer</c> says the same of itself.
    /// </summary>
    private static HashSet<string> CalledMemberNames(Type t)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var opMap = new Dictionary<short, System.Reflection.Emit.OpCode>();
        foreach (var f in typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.FieldType == typeof(System.Reflection.Emit.OpCode))
            {
                var op = (System.Reflection.Emit.OpCode)f.GetValue(null)!;
                opMap[op.Value] = op;
            }

        var types = new List<Type> { t };
        try { types.AddRange(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)); } catch { }

        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                             | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var ty in types)
        {
            IEnumerable<MethodBase> members;
            try { members = ty.GetMethods(F).Concat<MethodBase>(ty.GetConstructors(F)); }
            catch { continue; }

            foreach (var m in members)
            {
                byte[]? il;
                try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { continue; }
                if (il == null) continue;

                // Full-width decode. A naive scan for the call opcode would also match operand BYTES
                // and resolve a token that is not a call at all — a false positive in a test whose whole
                // job is to say "this call is present".
                for (int i = 0; i < il.Length;)
                {
                    short code = il[i];
                    if (il[i] == 0xFE && i + 1 < il.Length) { code = unchecked((short)(0xFE00 | il[i + 1])); i += 2; }
                    else i += 1;
                    if (!opMap.TryGetValue(code, out var op)) break;

                    int token = 0;
                    switch (op.OperandType)
                    {
                        case System.Reflection.Emit.OperandType.InlineNone: break;
                        case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                        case System.Reflection.Emit.OperandType.ShortInlineI:
                        case System.Reflection.Emit.OperandType.ShortInlineVar: i += 1; break;
                        case System.Reflection.Emit.OperandType.InlineVar: i += 2; break;
                        case System.Reflection.Emit.OperandType.InlineBrTarget:
                        case System.Reflection.Emit.OperandType.InlineField:
                        case System.Reflection.Emit.OperandType.InlineI:
                        case System.Reflection.Emit.OperandType.InlineMethod:
                        case System.Reflection.Emit.OperandType.InlineSig:
                        case System.Reflection.Emit.OperandType.InlineString:
                        case System.Reflection.Emit.OperandType.InlineTok:
                        case System.Reflection.Emit.OperandType.InlineType:
                        case System.Reflection.Emit.OperandType.ShortInlineR:
                            token = BitConverter.ToInt32(il, i); i += 4; break;
                        case System.Reflection.Emit.OperandType.InlineI8:
                        case System.Reflection.Emit.OperandType.InlineR: i += 8; break;
                        case System.Reflection.Emit.OperandType.InlineSwitch:
                            { int n = BitConverter.ToInt32(il, i); i += 4 + 4 * n; break; }
                        default: i = il.Length; break;
                    }

                    if (op != System.Reflection.Emit.OpCodes.Call
                     && op != System.Reflection.Emit.OpCodes.Callvirt
                     && op != System.Reflection.Emit.OpCodes.Newobj) continue;

                    try
                    {
                        // ⚠ THE ONE-ARGUMENT OVERLOAD IS A DELIBERATE NARROW CHOICE, NOT THE RIGHT
                        // DEFAULT. ResolveMethod(token) THROWS for any token inside a GENERIC CONTEXT;
                        // the correct general form passes the context, as SessionSafetyAnalyzer does:
                        //     mod.ResolveMethod(token, declaringType.GetGenericArguments(),
                        //                              method.GetGenericArguments())
                        // It is safe HERE for one reason only: this walks a single non-generic component
                        // type, and the assertion above is Contain — so a token that fails to resolve
                        // leaves the call absent from the set and the test goes RED. It fails LOUD.
                        //
                        // ⚠⚠ THAT SAFETY IS A PROPERTY OF THE ASSERTION, NOT OF THE OVERLOAD. In a
                        // CENSUS — where absence reads as clean — the same shortcut silently drops every
                        // call site inside a generic type, and in an assembly full of async state
                        // machines that is a short debt list and a clean bill of health. If anyone
                        // widens this helper beyond one non-generic type, or reuses it where absence
                        // means "nothing to report", THE OVERLOAD MUST CHANGE WITH IT.
                        var callee = m.Module.ResolveMethod(token);
                        if (callee?.DeclaringType?.FullName is string owner)
                            found.Add(owner + "." + callee.Name);
                    }
                    catch { /* a token this context cannot resolve is not a call we can name */ }
                }
            }
        }
        return found;
    }
}
