/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT: <b>a count measured off the PRODUCT ASSEMBLY is specific to the build configuration
/// it was measured in. A test that pins such a count must either refuse a configuration its numbers
/// were not frozen in, or not pin a number at all.</b>
///
/// <para><b>PROVED 2026-09-16 at <c>7b997b4</c>, not assumed.</b> The S-1 session-safety census, run
/// against four deliberately built assemblies at the IDENTICAL commit, reports <b>120</b> command
/// sites in Release-full and <b>117</b> in Debug-full; <b>97</b> in Release-community and <b>94</b> in
/// Debug-community. Methods (96/96/73/73), prefixes, unguarded and direct totals do not move — only
/// the site count does. Root cause, measured in the IL: Roslyn's async rewriter MERGES hoisted
/// state-machine fields for same-typed locals with non-overlapping lifetimes when optimisations are
/// ON and keeps one field per authored local when they are OFF, and a taint set keyed by field token
/// cannot tell two variables sharing one field apart. Nothing in either count recorded which
/// configuration produced it. Measured by the lane <c>il-count-configuration-dependence</c>,
/// 2026-09-16, on a four-cell build matrix (Debug|Release × full|community) whose four product hashes
/// were re-derived before use.</para>
///
/// <para><b>WHY THIS IS A SHARED TYPE AND NOT A COPY PER CONSUMER.</b> Three test classes pin a number
/// measured off the product assembly and the fix has to hold at all three, so per the repo's
/// generation-discipline rule it ships as ONE thing they all call plus a census that enumerates them
/// from the code. Three hand-edited copies of a configuration check is how a SET gets shipped as an
/// INSTANCE — and this repo has already measured that failure four times in one week.</para>
///
/// <para><b>⚠ THIS DELIBERATELY DOES NOT SHARE THE IL PRIMITIVES.</b> The house convention, written
/// down at <c>CheckBadgeClassificationTests.CalledMemberNames</c> and on
/// <c>SessionSafetyAnalyzer</c> itself, is that each analyser keeps its OWN copy of the IL decoding
/// primitives, so a change made for one invariant cannot quietly alter another invariant's reading.
/// That reason is sound and is left intact: nothing here decodes IL or changes what any analyser
/// reads. What is shared is one PREDICATE over one assembly-level attribute. It cannot shift a
/// reading — it either lets a class report or fails the class outright.</para>
///
/// <para><b>WHO MUST CALL THIS is enforced, not documented.</b>
/// <see cref="ProductCountGuardCensusTests"/> walks the test assembly's own IL, enumerates every test
/// type that reaches an IL-body or PE-metadata read of the product assembly, and goes RED when one of
/// them neither reaches this guard nor sits on an explicit exemption list. Adding a fourth consumer
/// without a guard is therefore a failing build, not a review miss.</para>
/// </summary>
internal static class ProductAssemblyBuildConfiguration
{
    /// <summary>
    /// The configuration every frozen number in this repo's product-assembly censuses was measured in,
    /// named once so a failure message cannot disagree with the guard. It is Release because that is
    /// what CI and <c>tools/record-full-profile-build.ps1</c> build; a bare <c>dotnet test</c> defaults
    /// to Debug and lands in the guard.
    /// </summary>
    internal const string FrozenConfiguration = "Release";

    /// <summary>
    /// Whether the given PRODUCT assembly was built with the JIT optimizer ON. Asked of the ASSEMBLY,
    /// never of a <c>#if</c>: the caller must get the right answer about whichever
    /// <c>SQLTriage.dll</c> the test host actually loaded, not about the configuration the test
    /// project happened to be compiled in.
    ///
    /// <para><b>⚠ A MISSING ATTRIBUTE MEANS OPTIMIZED, which is the opposite of how it reads.</b>
    /// <see cref="DebuggableAttribute"/> is emitted in order to turn JIT optimization OFF; an assembly
    /// carrying none is optimized by default. So <c>null</c> must answer "optimized" here and never
    /// "unknown" — reading absence as a failure would fire the guard on assemblies it has no complaint
    /// about.</para>
    ///
    /// <para><b>MEASURED 2026-09-16 on all four built cells, not assumed.</b> <c>-c Debug</c>:
    /// attribute PRESENT, <c>IsJITOptimizerDisabled=True</c>, flags <c>Default,
    /// IgnoreSymbolStoreSequencePoints, EnableEditAndContinue, DisableOptimizations</c>. <c>-c
    /// Release</c>: attribute PRESENT, <c>IsJITOptimizerDisabled=False</c>, flags
    /// <c>IgnoreSymbolStoreSequencePoints</c>. It discriminates on both profiles, and it is PRESENT on
    /// both axes — the null branch is defensive, not the observed case for this product.</para>
    /// </summary>
    internal static bool WasBuiltOptimized(Assembly product)
    {
        var debuggable = product.GetCustomAttribute<DebuggableAttribute>();
        return debuggable is null || !debuggable.IsJITOptimizerDisabled;
    }

    /// <summary>
    /// What was read off the assembly, in one line, so every caller's output records the provenance of
    /// its own verdict rather than leaving the reader to guess which DLL answered. A PATH is not an
    /// identity on this box, so the path is printed as something to CHECK, never as proof.
    /// </summary>
    internal static string Describe(Assembly product)
    {
        var debuggable = product.GetCustomAttribute<DebuggableAttribute>();
        var name = product.GetName();
        return $"{name.Name} {name.Version} "
             + (debuggable is null
                    ? "DebuggableAttribute ABSENT (absence means the JIT optimizes)"
                    : $"DebuggableAttribute IsJITOptimizerDisabled={debuggable.IsJITOptimizerDisabled}"
                      + $" flags={debuggable.DebuggingFlags}")
             + $" -> optimized={WasBuiltOptimized(product)}"
             + $" | location to CHECK: {SafeLocation(product)}";
    }

    private static string SafeLocation(Assembly product)
    {
        try { return string.IsNullOrEmpty(product.Location) ? "(no location: loaded from memory)" : product.Location; }
        catch (NotSupportedException) { return "(location unavailable)"; }
    }

    /// <summary>
    /// THE ONE GUARD. Call it before believing any number this repo froze by MEASUREMENT against the
    /// product assembly.
    ///
    /// <para><b>IT FAILS; IT NEVER SKIPS.</b> No <c>Skip</c>, no conditional <c>[Fact]</c>, no early
    /// <c>return</c> — a skipped test renders identically to a passing one, and a green a census did
    /// not earn is the exact thing this exists to prevent. That is also why it is not an
    /// <c>Assert.Skip</c> shaped helper: there is no safe way to spell "this census had nothing to
    /// say" that a reader will not read as "this census was happy".</para>
    ///
    /// <para><b>THE FAILURE TEXT IS A SAFETY SURFACE.</b> It says which configuration the numbers were
    /// frozen in, it NAMES WHAT TO CHECK, and the one action it prescribes — re-run in Release — is
    /// safe in every case including the case where this guard is itself the false positive. It
    /// deliberately does NOT tell the reader to change anything in the product, the csproj, or the
    /// frozen numbers.</para>
    /// </summary>
    /// <param name="product">
    /// The product assembly the caller's numbers were measured against — normally
    /// <c>typeof(ConfigFileHelper).Assembly</c>, resolved from a type the test project binds, so the
    /// verdict cannot drift onto a stale DLL in some other <c>bin\</c>.
    /// </param>
    /// <param name="output">Where the provenance line goes, so a PASS is auditable too, not only a fail.</param>
    /// <param name="whatIsFrozen">
    /// The caller's own frozen numbers, named — e.g.
    /// "HasEmbeddedPortablePdb / DocumentRows / MethodDebugInformationRows / the 155-document residual".
    /// It is a required argument rather than a generic sentence because the reader of a failure needs
    /// to know WHICH numbers are void, and only the caller knows.
    /// </param>
    internal static void RequireTheBuildConfigurationTheseNumbersWereFrozenIn(
        Assembly product, ITestOutputHelper output, string whatIsFrozen)
    {
        if (product is null) throw new ArgumentNullException(nameof(product));
        if (string.IsNullOrWhiteSpace(whatIsFrozen))
            throw new ArgumentException(
                "name the frozen numbers this guard is protecting: a failure that cannot say WHICH "
                + "numbers are void sends the reader looking in the wrong place.", nameof(whatIsFrozen));

        output?.WriteLine("PRODUCT ASSEMBLY UNDER CENSUS: " + Describe(product));

        WasBuiltOptimized(product).Should().BeTrue(
            "THIS TEST WAS RUN AGAINST A PRODUCT ASSEMBLY BUILT IN A CONFIGURATION ITS NUMBERS WERE "
            + "NOT FROZEN IN, so nothing it reports is evidence. The assembly carries "
            + "DebuggableAttribute with the JIT optimizer DISABLED - a Debug build - while these "
            + "numbers were frozen against a " + FrozenConfiguration + " build: " + whatIsFrozen
            + ". WHY THAT MATTERS, MEASURED 2026-09-16 at 7b997b4 on four built cells: the identical "
            + "commit reports 120 command sites in Release-full and 117 in Debug-full (97 vs 94 on the "
            + "community profile). Roslyn merges hoisted async state-machine fields when optimisations "
            + "are ON and keeps one per authored local when they are OFF, so a count that walks IL "
            + "moves with the configuration on its own. WHAT TO CHECK, in this order. (1) The "
            + "configuration: re-run this class with -c " + FrozenConfiguration + ", which is what CI "
            + "and tools/record-full-profile-build.ps1 run - a bare 'dotnet test' defaults to Debug "
            + "and lands here. (2) If you believe you ARE in " + FrozenConfiguration + " and this "
            + "fired anyway, then what to check is the ASSEMBLY, not the numbers: this reads the "
            + "attribute off whichever SQLTriage.dll the test host loaded, so a stale DLL left in a "
            + "bin\\ directory by an earlier build, or a csproj that sets Optimize=false in this "
            + "configuration, gives exactly this failure. The provenance line printed above says which "
            + "file answered. DO NOT EDIT THE FROZEN NUMBERS TO MATCH WHAT A DEBUG RUN REPORTS: that "
            + "repair is attractive and it reverts the census to an undercount, turning CI and main "
            + "RED while reading green on your machine. Guard: "
            + nameof(ProductAssemblyBuildConfiguration) + "."
            + nameof(RequireTheBuildConfigurationTheseNumbersWereFrozenIn)
            + "; the census that enforces every consumer calls it is "
            + nameof(ProductCountGuardCensusTests) + ".");
    }
}
