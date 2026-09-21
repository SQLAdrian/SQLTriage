/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT: <b>every test type whose verdict can rest on a number read out of the PRODUCT
/// assembly's IL or PE metadata must refuse a build configuration those numbers were not frozen in.</b>
/// <see cref="ProductAssemblyBuildConfiguration.RequireTheBuildConfigurationTheseNumbersWereFrozenIn"/>
/// is the one definition of that refusal.
///
/// <para><b>WHY A CENSUS AND NOT THREE EDITS.</b> Three classes pin such a number today and only ONE
/// of them checked the configuration. The other two were found by measurement, not by review:
/// <c>EmbeddedSourceCensusTests</c> fails four of six tests in Debug and one of its assertions PASSED
/// VACUOUSLY there, and <c>ConfigStoreI1CensusTests</c> pins a floor with no configuration check at
/// all. Hand-editing three copies would leave the fourth consumer — the one nobody has written yet —
/// exactly as exposed as these three were. So the set is enumerated FROM THE CODE: this class walks
/// the test assembly's own IL, and a new unguarded pinner is a failing build rather than a review
/// miss.</para>
///
/// <para><b>WHAT MAKES IT STRUCTURAL, and why that is not a formality.</b>
/// <see cref="ProductCountGuardReachAnalyzer"/> resolves call, field and type tokens through
/// <see cref="Module"/> and follows call edges to a fixpoint. It never matches source characters.
/// MEASURED 2026-09-16: a character grep for <c>MethodBody</c> named EIGHT test files as IL walkers
/// and structurally THREE are — the other five declare their own source-text helper of that name. A
/// grep-based version of this census would have demanded guards on five files that read no assembly
/// and would have missed all three files that actually pin the counts, because the file that walks the
/// IL and the file that pins the number are different files. This repo has also measured a character
/// census defeated by a LINE WRAP, in both directions, on 2026-09-11. Nothing here can be moved by a
/// reformat, a rename, a receiver's spelling or an extracted local.</para>
///
/// <para><b>THE GRANULARITY IS THE TYPE, deliberately.</b> A per-METHOD rule would demand a
/// configuration guard on assertions that have nothing to do with the product assembly — the
/// prose-length floor in <c>ConfigStoreI1CensusTests</c>, a positive-control test's own liveness check
/// — and a guard demanded where it is meaningless is a guard that gets deleted. The cost is named:
/// within a type that already reaches the guard, a later test method can pin a new product count
/// without calling it. That residual is why the guard is also documented at each analyser's
/// declaration, where the next narrow agent will be standing.</para>
///
/// <para><b>THIS CLASS PINS NO FROZEN MAGNITUDE and therefore needs no configuration guard of its
/// own.</b> Every number it asserts is either an emptiness (no unguarded pinner) or a liveness floor
/// (the walk decoded something). It also reads the TEST assembly, not the product, so it is excluded
/// from its own population by the same structural rule it applies to everything else — and
/// <see cref="The_reach_analyser_is_alive_and_classifies_its_specimens_correctly"/> asserts that
/// exclusion rather than assuming it. Proved 2026-09-16: this class passes in Debug and in Release,
/// on both profiles.</para>
///
/// <para><b>⚠ WHAT THIS PROVES IS THAT THE CLASS ASKS, NOT THAT IT REFUSES.</b> Reaching a read of
/// <see cref="System.Diagnostics.DebuggableAttribute"/> is structurally detectable; whether the answer
/// is then ASSERTED is not — a class could read the attribute, print it and carry on, and this census
/// would call it guarded. The refusal itself is proved a different way, by exercise rather than by
/// structure: run the guarded classes against a Debug build and watch them FAIL. MEASURED 2026-09-16
/// on the four-cell matrix, which is the evidence for the behaviour half of this pair. Stating the
/// division plainly because a census that silently claims the stronger property is the failure mode
/// this file exists to prevent.</para>
///
/// <para><b>THE BOUNDARY, recorded rather than silently dropped.</b> The population is types that read
/// IL BODIES or PE METADATA. A pinned REFLECTED count is configuration-specific too — measured
/// 2026-09-16, Debug-full carries 3,849 types and 29,570 methods with IL against Release-full's 3,825
/// and 28,444 — and 51 of 506 test files reach reflective enumeration APIs. This census PRINTS that
/// wider set (see <see cref="Census_is_recorded_for_the_gate"/>) and asserts nothing about it. Of the
/// four likeliest pinners in it, none pins an assembly-derived count (checked 2026-09-16 by a
/// character screen, which is a TRIPWIRE and not a guarantee — so "no other unguarded pinner exists
/// among the 51" is BELIEVED, not proved). Widening is a deliberate decision with its own lane.</para>
/// </summary>
public sealed class ProductCountGuardCensusTests
{
    private readonly ITestOutputHelper _out;
    public ProductCountGuardCensusTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The test assembly whose IL this census walks — its own.
    /// </summary>
    private static Assembly TestAssembly => typeof(ProductCountGuardCensusTests).Assembly;

    /// <summary>
    /// The product assembly, resolved from this assembly's REFERENCE to it rather than from
    /// <c>typeof(ConfigFileHelper).Assembly</c>.
    ///
    /// <para><b>⚠ THAT IS NOT A STYLE CHOICE.</b> Binding a product type here would put a product
    /// metadata token in this class's own IL, which is one half of the population rule this class
    /// enforces — so this census would enumerate ITSELF as an unguarded pinner and then demand a
    /// configuration guard for numbers that describe the TEST assembly. Reading the reference keeps
    /// this class outside its own population for a structural reason, and
    /// <see cref="The_reach_analyser_is_alive_and_classifies_its_specimens_correctly"/> asserts that
    /// it is. The cost of resolving by name is that a rename of the product assembly turns the lookup
    /// into a miss, so the miss FAILS and prints every referenced name.</para>
    /// </summary>
    private Assembly ResolveProduct()
    {
        var names = TestAssembly.GetReferencedAssemblies();
        var reference = names.FirstOrDefault(n => n.Name == "SQLTriage");

        reference.Should().NotBeNull(
            "this census is about counts measured off the PRODUCT assembly, so it cannot run without "
            + "one. No referenced assembly is named SQLTriage. If the product assembly was renamed, "
            + "this lookup is what to change - do NOT replace it with typeof(a product type), which "
            + "would put this class into its own population. Referenced assemblies were: "
            + string.Join(", ", names.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal)));

        var product = Assembly.Load(reference!);
        _out.WriteLine("PRODUCT ASSEMBLY: " + ProductAssemblyBuildConfiguration.Describe(product));
        return product;
    }

    /// <summary>
    /// The walk, done once per process and re-asserted per test. Decoding this assembly's ~17,000
    /// method bodies four times would cost four times as long for four identical answers; the
    /// haystack assertions below still run for every test, so a cached EMPTY walk cannot pass one.
    /// </summary>
    private static ProductCountGuardReachAnalyzer.Report? _walk;
    private static readonly object WalkGate = new();

    private ProductCountGuardReachAnalyzer.Report ReadReach()
    {
        ProductCountGuardReachAnalyzer.Report report;
        lock (WalkGate)
        {
            report = _walk ??= ProductCountGuardReachAnalyzer.Read(TestAssembly, ResolveProduct());
        }

        _out.WriteLine($"TEST ASSEMBLY WALK: types={report.TypesSeen} methodBodies={report.MethodBodiesDecoded:N0} "
                     + $"instructions={report.InstructionsDecoded:N0} tokensResolved={report.TokensResolved:N0} "
                     + $"unresolvedTokens={report.UnresolvedTokens:N0} callEdges={report.CallEdges:N0}");
        _out.WriteLine($"PRODUCT ASSEMBLY: {report.ProductAssembly}, types={report.ProductTypesSeen:N0}");

        // ⚠ HAYSTACK BEFORE NEEDLE, and it is asserted here rather than left to the caller because
        // EVERY assertion in this class is vacuously green on an empty walk. A decoder that stops on
        // the first instruction, a reflection load that yields no types, a filter that matches nothing:
        // all three produce "no unguarded pinners found", which is this census's PASS.
        report.MethodBodiesDecoded.Should().BeGreaterThan(1000,
            "the walk must decode this assembly's method bodies before any conclusion about which "
            + "types read the product assembly means anything. It decoded "
            + $"{report.MethodBodiesDecoded}. MEASURED 2026-09-16: 17,074 bodies in Release-full, "
            + "18,155 in Debug-full, 13,414 and 14,145 on the community profile. A number near zero "
            + "means the walk is dead, not that the assembly is clean - WHAT TO CHECK is whether "
            + "reflection over this assembly threw (the analyser reports type-load failures rather "
            + "than swallowing them) before touching anything in the population below.");
        report.CallEdges.Should().BeGreaterThan(1000,
            "the reach of every flag in this census travels along resolved call edges, so a call "
            + "graph with no edges silently collapses every 'reaches' answer to the DIRECT answer - "
            + "which would report all three census consumers as unguarded and all three analysers as "
            + "the only readers. Edges found: " + report.CallEdges);
        report.ProductTypesSeen.Should().BeGreaterThan(1000,
            "the product assembly must load and enumerate, or the population rule's product half "
            + "cannot discriminate. Types seen: " + report.ProductTypesSeen + ". MEASURED 2026-09-16: "
            + "3,825 (Release-full), 3,849 (Debug-full), 2,926 and 2,943 on community.");

        return report;
    }

    // ── The exemption list ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test types that read the product assembly's IL or metadata and are NOT required to refuse a
    /// foreign build configuration, each with the reason written here rather than left to be inferred.
    ///
    /// <para><b>AN ENTRY HERE IS A CLAIM, AND THE CLAIM IS CHECKED.</b> The only admissible reason is
    /// the second branch of the invariant — the type pins no number, so there is no frozen magnitude
    /// for a configuration to invalidate. <see cref="The_exemption_list_holds_only_types_that_pin_no_number"/>
    /// asserts exactly that, structurally, so an exemption that stops being true goes RED instead of
    /// quietly widening. "Measured insensitive today" is deliberately NOT an admissible reason: a
    /// count that does not move on this week's codegen is still a count with no record of what it was
    /// measured in.</para>
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        // Reads the compiled Checks component's IL to prove the page CALLS the shared badge helper,
        // and asserts the PRESENCE of two resolved call targets with its own haystack control first.
        // It pins no number at all, so there is nothing here for a configuration to invalidate: the
        // calls it looks for are emitted in Debug and Release alike. This is the worked example of the
        // invariant's other branch - the cheapest way to be immune to this whole class of defect is to
        // assert a SET rather than a COUNT.
        ["SQLTriage.Tests.CheckBadgeClassificationTests"] =
            "pins no number: asserts two resolved call targets are PRESENT in the compiled component, "
            + "with a non-empty haystack asserted first. Nothing frozen, nothing to invalidate.",

        // Decodes AlertEvaluationService's IL to enumerate keyed-collection accesses and firing paths
        // (lane alert-stamp-key-per-server). Every assertion is over a SET or an EMPTINESS: each keyed
        // collection is classified, no method mixes a per-server state with a per-alert clock without
        // declaring it, every firing path reaches the measurement stamp, the known-good members are
        // still visible, and each haystack is non-empty first. It prints a "method(s) walked" figure
        // that differs by configuration (Release 190, Debug 210, measured 2026-09-16) and asserts
        // nothing on it, which is exactly why that figure is safe to leave configuration-dependent.
        ["SQLTriage.Tests.AlertStampKeyCensusTests"] =
            "pins no number: asserts that enumerated sets are complete and that finding lists are "
            + "empty, each haystack asserted non-empty first. The walked-method count is printed, "
            + "never asserted. Nothing frozen, nothing to invalidate.",
    };

    // ── 1. THE invariant ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE REAL GUARD IN THIS FILE. Every test type in the population either reaches a read of the
    /// product assembly's <see cref="System.Diagnostics.DebuggableAttribute"/> — which is the only
    /// thing on an assembly that discriminates Debug from Release — or is on
    /// <see cref="Exempt"/> with its reason.
    ///
    /// <para><b>IT CHECKS THE BEHAVIOUR, NOT THE HELPER'S IDENTITY.</b> A type qualifies by reaching
    /// the configuration read at all, however it spells it. That is required rather than lenient:
    /// <c>SessionSafetyCensusTests</c> carries its own equivalent check and belongs to a different
    /// lane's lock, so a census keyed to the shared helper's name would either fail that class for a
    /// defect it does not have, or force an edit across a lane boundary. New consumers should call
    /// <see cref="ProductAssemblyBuildConfiguration.RequireTheBuildConfigurationTheseNumbersWereFrozenIn"/>;
    /// <see cref="Census_is_recorded_for_the_gate"/> prints which population types do and which carry
    /// their own copy, so the remaining duplication is on the record.</para>
    /// </summary>
    [Fact]
    public void Every_test_type_that_counts_the_product_assembly_refuses_a_foreign_build_configuration()
    {
        var report = ReadReach();
        var population = report.Population.ToList();

        _out.WriteLine($"POPULATION: {population.Count} test type(s) reading product IL or PE metadata");
        foreach (var t in population)
            _out.WriteLine($"    {t.TypeName}: ilRead={t.ReachesIlRead} peRead={t.ReachesPeRead} "
                         + $"configRead={t.ReachesConfigurationRead} numericPin={t.ReachesNumericPin} "
                         + $"exempt={Exempt.ContainsKey(t.TypeName)}"
                         + (t.IlReadWitness is null ? "" : $" ilVia={t.IlReadWitness}")
                         + (t.PeReadWitness is null ? "" : $" peVia={t.PeReadWitness}")
                         + (t.ConfigurationReadWitness is null ? "" : $" configVia={t.ConfigurationReadWitness}"));

        // The needle only means something because the haystack is asserted: the three consumers this
        // lane measured must all be IN the population before an emptiness result is believable.
        population.Count.Should().BeGreaterThan(2,
            "at least three test types are known BY MEASUREMENT to read the product assembly's IL or "
            + "PE metadata (SessionSafetyCensusTests, ConfigStoreI1CensusTests, "
            + "EmbeddedSourceCensusTests, plus CheckBadgeClassificationTests which pins no number). "
            + "Finding fewer means the population rule stopped matching - WHAT TO CHECK is the "
            + "classifier in ProductCountGuardReachAnalyzer.Classify and the call-graph edge count "
            + "printed above, NOT the guards. A census that finds nobody reports no offenders.");

        var unguarded = population
            .Where(t => !t.ReachesConfigurationRead && !Exempt.ContainsKey(t.TypeName))
            .OrderBy(t => t.TypeName, StringComparer.Ordinal)
            .ToList();

        foreach (var t in unguarded) _out.WriteLine("UNGUARDED PINNER: " + t.Line);

        unguarded.Should().BeEmpty(
            "these test types read the PRODUCT assembly's IL or PE metadata and never ask which build "
            + "configuration produced it, so any number they pin is evidence about nothing: "
            + string.Join(", ", unguarded.Select(t => t.TypeName)) + ". MEASURED 2026-09-16 at "
            + "7b997b4 on four built cells: the same commit yields 120 command sites in Release-full "
            + "and 117 in Debug-full, and the embedded-source census reads 155 source documents in "
            + "Release-full and ZERO in Debug-full because Release embeds the portable PDB and Debug "
            + "ships it beside the assembly. WHAT TO CHECK, for each type listed: whether its numbers "
            + "are frozen by measurement against a build, and if they are, have it call "
            + nameof(ProductAssemblyBuildConfiguration) + "."
            + nameof(ProductAssemblyBuildConfiguration.RequireTheBuildConfigurationTheseNumbersWereFrozenIn)
            + " before it believes them. If instead the type pins NO number - it asserts a set, a "
            + "presence or an emptiness - then add it to this class's Exempt list with that reason, "
            + "which " + nameof(The_exemption_list_holds_only_types_that_pin_no_number) + " then "
            + "checks. Do NOT satisfy this by deleting a count that is doing real work.");
    }

    // ── 2. The exemption list polices itself ──────────────────────────────────────────────────────

    /// <summary>
    /// An exemption is admissible only because the type pins no number. This asserts that premise from
    /// structure — FluentAssertions' numeric assertions, its collection COUNT assertions, and xunit's
    /// numeric <c>Equal</c>/<c>InRange</c>/<c>Single</c>, all identified by resolved declaring type —
    /// so the day someone adds a frozen count to an exempt class, the exemption goes RED rather than
    /// silently covering it.
    ///
    /// <para>Emptiness assertions are deliberately not counted as pins. A census whose only numbers
    /// are "this is empty" and "this is non-empty" cannot drift with codegen, which is the entire
    /// reason the invariant offers "do not pin a number" as an equal alternative to the guard.</para>
    /// </summary>
    [Fact]
    public void The_exemption_list_holds_only_types_that_pin_no_number()
    {
        var report = ReadReach();

        // The detector must be ALIVE before its silence is evidence. If FluentAssertions' type layout
        // changes under a package upgrade, this control fails LOUDLY instead of the rule below turning
        // into "no exempt type pins a number because nothing pins a number".
        var knownPinners = new[] { "SQLTriage.Tests.ConfigStoreI1CensusTests", "SQLTriage.Tests.SessionSafetyCensusTests" };
        foreach (var name in knownPinners)
        {
            var known = report[name];
            known.Should().NotBeNull($"specimen control: {name} must be present in this assembly's walk");
            known!.ReachesNumericPin.Should().BeTrue(
                $"specimen control: {name} pins product-derived counts by measurement (a census floor, "
                + "a site floor) and the numeric-assertion detector must see them, or its silence "
                + "about the exempt types below means nothing. WHAT TO CHECK: the FluentAssertions "
                + "and xunit rules in ProductCountGuardReachAnalyzer.Classify against the assertion "
                + "APIs printed by " + nameof(Census_is_recorded_for_the_gate) + ".");
        }

        var exemptButPinning = Exempt.Keys
            .Select(name => report[name])
            .Where(t => t != null && t!.ReachesNumericPin)
            .Select(t => t!)
            .ToList();

        foreach (var t in exemptButPinning)
            _out.WriteLine($"EXEMPTION NO LONGER TRUE: {t.Line} numericVia={t.NumericPinWitness} "
                         + $"apis=[{string.Join(", ", t.NumericApis)}]");

        exemptButPinning.Should().BeEmpty(
            "every entry on this class's Exempt list claims the type pins NO number, and these now "
            + "pin one: " + string.Join(", ", exemptButPinning.Select(t => t.TypeName)) + ". An "
            + "exemption whose premise has expired is worse than no exemption, because it reads as a "
            + "decision someone made about the count that is now there. WHAT TO CHECK: whether the "
            + "new number is frozen by measurement against the product assembly. If it is, remove the "
            + "exemption and have the type call the shared guard; if the number is a liveness floor "
            + "(non-empty, greater than zero) it is not a frozen magnitude and the assertion APIs "
            + "listed above will say which call was matched.");

        // A stale exemption for a type that no longer exists is dead weight that reads as coverage.
        var ghosts = Exempt.Keys.Where(name => report[name] is null).ToList();
        ghosts.Should().BeEmpty(
            "these exempted type names are not in this assembly: " + string.Join(", ", ghosts)
            + ". A name that resolves to nothing is the defect this repo has measured three times in "
            + "comments citing tests that do not exist - the next reader greps it, finds nothing, and "
            + "either writes a duplicate or stops trusting the list. Delete the entry or fix the name.");
    }

    // ── 3. The instrument's own controls ──────────────────────────────────────────────────────────

    /// <summary>
    /// Specimen controls for the classifier, in both directions, because a census that cannot tell a
    /// name match from a structural read is the instrument this lane was created to replace.
    ///
    /// <para>POSITIVE: the three consumers measured to read the product assembly are classified as
    /// reading it. NEGATIVE: <c>RemediationBatchPreviewUiTests</c>, whose own helper is called
    /// <c>TryGetMethodBody</c> and which a character grep named as an IL walker, must be classified as
    /// performing NO direct IL read — MEASURED 2026-09-16 in all four cells as
    /// <c>ilBodyApi=0, opCodesField=0, peMetadataApi=0</c>. EXCLUSION: this census type itself must
    /// sit outside its own population.</para>
    /// </summary>
    [Fact]
    public void The_reach_analyser_is_alive_and_classifies_its_specimens_correctly()
    {
        var report = ReadReach();

        foreach (var name in new[] { "SQLTriage.Tests.SessionSafetyCensusTests",
                                     "SQLTriage.Tests.ConfigStoreI1CensusTests",
                                     "SQLTriage.Tests.EmbeddedSourceCensusTests",
                                     "SQLTriage.Tests.CheckBadgeClassificationTests" })
        {
            var t = report[name];
            t.Should().NotBeNull($"specimen control: {name} must exist in this assembly");
            _out.WriteLine("POSITIVE SPECIMEN: " + t!.Line);
            t.IsPopulation.Should().BeTrue(
                $"specimen control: {name} is MEASURED to read the product assembly's IL or PE "
                + "metadata (2026-09-16, four cells) and must therefore be in the population. If it "
                + "is not, the classifier narrowed - WHAT TO CHECK is which half of the rule failed: "
                + $"isTestType={t.IsTestType}, reachesProduct={t.ReachesProductReference}, "
                + $"il={t.ReachesIlRead}, pe={t.ReachesPeRead}.");
        }

        // The DIRECT counter must be able to be non-zero before a zero from it means anything. This is
        // the other half of the negative control below: same field, same instrument, one type reading
        // IL in its own body and one only appearing to. Without this pair, "OwnIlReads == 0" would
        // pass for every type in the assembly if the counter were never incremented - which is exactly
        // the defect this instrument had while it was being written.
        var directReader = report["SQLTriage.Tests.CheckBadgeClassificationTests"];
        _out.WriteLine($"DIRECT-READ CONTROL: {directReader!.TypeName} ownIlReads={directReader.OwnIlReads}");
        directReader.OwnIlReads.Should().BeGreaterThan(0,
            "CheckBadgeClassificationTests decodes the compiled Checks component's IL in its own "
            + "CalledMemberNames helper (MEASURED 2026-09-16: ilBodyApi=3, opCodesField=3 in all four "
            + "cells), so the DIRECT hit counter must be non-zero for it. If this is zero the counter "
            + "is not being incremented and the negative control below cannot fail.");

        var nameMatchOnly = report["SQLTriage.Tests.RemediationBatchPreviewUiTests"];
        nameMatchOnly.Should().NotBeNull("negative control: RemediationBatchPreviewUiTests must exist");
        _out.WriteLine("NEGATIVE SPECIMEN: " + nameMatchOnly!.Line);
        nameMatchOnly.OwnIlReads.Should().Be(0,
            "negative control. This class declares its own source-text helper TryGetMethodBody and a "
            + "character grep for MethodBody therefore named it an IL walker; structurally it reads "
            + "no IL at all (MEASURED 2026-09-16: ilBodyApi=0 in all four cells). If this is now "
            + "non-zero, either the class genuinely started reading IL - in which case it belongs in "
            + "the population and the guard question applies to it - or this census has regressed to "
            + "matching NAMES, which is the defect it exists to replace. Check which API was matched "
            + $"before changing anything: [{string.Join(", ", nameMatchOnly.IlApis)}]");

        var self = report["SQLTriage.Tests.ProductCountGuardCensusTests"];
        self.Should().NotBeNull("exclusion control: this census type must appear in its own walk");
        _out.WriteLine("EXCLUSION CONTROL (this census): " + self!.Line);
        self.ReachesProductReference.Should().BeFalse(
            "this census counts the TEST assembly, and its numbers were never frozen against a "
            + "product build configuration - so it must stay outside its own population, and it does "
            + "that by holding no reference to a product type. If this is now true, something in this "
            + "class bound a product type (typeof(ConfigFileHelper) is the usual way) and this census "
            + "is about to demand a configuration guard for numbers that describe the test assembly. "
            + "WHAT TO CHECK: resolve the product assembly through GetReferencedAssemblies, as "
            + "ResolveProduct does, rather than through a product type.");
        self.IsPopulation.Should().BeFalse("and therefore it is not in its own population");
    }

    // ── 4. The record ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The WHOLE distribution, every type in the test assembly with its counts including zeros, so the
    /// gate can audit the classification instead of trusting a filtered subset. A filter cannot be
    /// audited: the reader of "3 IL readers found" cannot tell it from "3 found, 2 dropped by a
    /// pattern".
    /// </summary>
    [Fact]
    public void Census_is_recorded_for_the_gate()
    {
        var report = ReadReach();

        _out.WriteLine("");
        _out.WriteLine("POPULATION AND HOW EACH MEMBER REFUSES A FOREIGN CONFIGURATION");
        foreach (var t in report.Population.OrderBy(t => t.TypeName, StringComparer.Ordinal))
        {
            var how = Exempt.ContainsKey(t.TypeName) ? "EXEMPT: " + Exempt[t.TypeName]
                    : !t.ReachesConfigurationRead ? "UNGUARDED"
                    : (t.ConfigurationReadWitness ?? "").Contains(nameof(ProductAssemblyBuildConfiguration))
                        ? "shared guard"
                        : "its OWN configuration check: " + t.ConfigurationReadWitness;
            _out.WriteLine($"    {t.TypeName} -> {how}");
        }

        _out.WriteLine("");
        _out.WriteLine("THE WIDER BOUNDARY, recorded and NOT asserted on: test types reaching reflective");
        _out.WriteLine("enumeration of an assembly. A pinned reflected count is configuration-specific too");
        _out.WriteLine("(Debug-full 3,849 types / 29,570 methods vs Release-full 3,825 / 28,444, measured");
        _out.WriteLine("2026-09-16), but this census does not reach them and widening is its own decision.");
        var wider = report.Types
            .Where(t => t.IsTestType && t.ReachesReflectiveEnumeration && !t.IsPopulation)
            .OrderBy(t => t.TypeName, StringComparer.Ordinal).ToList();
        _out.WriteLine($"    count={wider.Count}");
        foreach (var t in wider) _out.WriteLine("    " + t.TypeName);

        _out.WriteLine("");
        _out.WriteLine("WHOLE DISTRIBUTION, every type, zeros included:");
        foreach (var t in report.Types) _out.WriteLine("    " + t.Line);

        _out.WriteLine("");
        _out.WriteLine("ASSERTION APIS SEEN, per population member:");
        foreach (var t in report.Population.OrderBy(t => t.TypeName, StringComparer.Ordinal))
        {
            _out.WriteLine($"    {t.TypeName}");
            _out.WriteLine($"        il      : {string.Join(", ", t.IlApis)}");
            _out.WriteLine($"        pe      : {string.Join(", ", t.PeApis)}");
            _out.WriteLine($"        config  : {string.Join(", ", t.ConfigApis)}");
            _out.WriteLine($"        numeric : {string.Join(", ", t.NumericApis)}");
        }

        // The record is only a record if the walk resolved what it read. Unresolved tokens are
        // REPORTED rather than swallowed: 153 of ~557,000 instructions (0.02%) on 2026-09-16.
        _out.WriteLine("");
        _out.WriteLine($"UNRESOLVED TOKENS: {report.UnresolvedTokens:N0} of {report.InstructionsDecoded:N0} "
                     + "instructions decoded. These are tokens this walk could not resolve to a member; "
                     + "each one is a potential missed classification, which is why the number is printed "
                     + "rather than discarded.");
        report.UnresolvedTokens.Should().BeLessThan(report.InstructionsDecoded / 20,
            "more than five percent of this assembly's tokens failed to resolve, so the classification "
            + "above is not trustworthy. WHAT TO CHECK: whether a dependency failed to load (the "
            + "analyser reports type-load failures rather than swallowing them) before believing any "
            + "conclusion in this class.");
    }
}
