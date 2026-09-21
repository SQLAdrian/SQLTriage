/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using SQLTriage.Data;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT: <b>(I1) a customer's configuration must never be silently replaced by a built-in
/// default.</b>
///
/// <para><b>WHY A CENSUS AND NOT ANOTHER FIXED TEST.</b> I1 has been fixed twice as an INSTANCE and has
/// come back both times somewhere else. On 2026-08-04 seven config stores were guarded and the boundary
/// was declared held; a cold gate then found three stores that were not on the hand-kept list at all —
/// <c>ServerConnectionManager</c>, which holds the only copies of the SQL passwords on the box,
/// <c>ScheduledTaskDefinitionService</c> and <c>AlertDefinitionService</c> — and an operator's data was
/// destroyed through each of them. On 2026-09-11 the lane <c>seeder-stub-freeze</c> fixed
/// <c>DashboardConfigService</c>'s absent-file arm and shipped no census, and this lane then found two
/// more live sites. A list a person maintains is the wrong instrument for a property that has to hold
/// at every store; what is needed is an enumerator that reads the code.</para>
///
/// <para><b>SCOPE, STATED.</b> This lane fixed two sites and shipped the census that makes a third
/// impossible to ADD silently. It did NOT adopt the store handle that
/// <see cref="StoreWriteIntent"/> describes — one object owning path, read, cached outcome and write,
/// with <c>Save</c> unreachable except through it. That is the complete fix, it is what would make the
/// boundary real rather than conventional, and across ~30 stores it is larger than one lane.</para>
///
/// <para><b>WHAT THIS CENSUS DOES NOT COVER, said plainly.</b> It keys on a failed READ reaching a
/// write. The second site this lane fixed —
/// <c>DashboardConfigService.LoadConfigFromDisk</c>'s <c>?? DefaultConfigGenerator.Generate()</c> —
/// was NOT of that shape: a file holding the JSON literal <c>null</c> parses without throwing, so
/// there was no failed read for this census to see, and the stub was served under a FALSE
/// <c>Origin = OperatorFile</c>. Nothing here would have caught it. It is pinned behaviourally instead,
/// by <c>DashboardConfigAbsentFileTests</c>.</para>
/// </summary>
public sealed class ConfigStoreI1CensusTests
{
    private readonly ITestOutputHelper _out;
    public ConfigStoreI1CensusTests(ITestOutputHelper output) => _out = output;

    private static Assembly Product => typeof(ConfigFileHelper).Assembly;

    /// <summary>
    /// THE CORE PIN, and it is checked in BOTH directions on purpose.
    ///
    /// <para>Forward: every method where a failed config read can reach a replacing write must carry
    /// <see cref="I1ReadFailureMayWriteAttribute"/> at its own declaration. A new one goes red with
    /// nothing for anyone to remember.</para>
    ///
    /// <para>Backward, and this is the half that stops the test lying: every method that CARRIES the
    /// attribute must still be found by the analyser. Break the IL walk, the opcode table, the
    /// reachability or the sink sets and the finding count drops to zero — which, with only a
    /// forward assertion, is the greenest possible result. The backward direction turns a dead
    /// instrument RED. (Five instruments reported success while answering their own question on
    /// 2026-09-10; this is the guard against being the sixth.)</para>
    /// </summary>
    [Fact]
    public void A_failed_config_read_never_reaches_a_replacing_write()
    {
        var findings = I1ConfigStoreAnalyzer.FailedReadReachesReplacingWrite(Product);
        _out.WriteLine($"findings={findings.Count}");
        foreach (var f in findings) _out.WriteLine("  " + f);

        var undeclared = findings
            .Where(f => f.Method.GetCustomAttribute<I1ReadFailureMayWriteAttribute>() == null)
            .Select(f => f.ToString())
            .Distinct()
            .ToList();

        undeclared.Should().BeEmpty(
            "a config store that answers a FAILED READ with a write is how an operator loses their "
            + "configuration: the damage was loud and the rewrite is quiet. Fix the method so the "
            + "failure arm writes nothing — ConfigFileHelper.Load already quarantines and reports the "
            + "outcome — or, if the write is genuinely not a built-in default landing on a customer's "
            + "file, declare it with [I1ReadFailureMayWrite(\"why\")] ON THE METHOD and say what it "
            + "preserves. Found: " + string.Join(" | ", undeclared));

        var declared = I1ConfigStoreAnalyzer.TypesOf(Product)
            .SelectMany(t =>
            {
                try
                {
                    return t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                        | BindingFlags.Static | BindingFlags.DeclaredOnly)
                            .Cast<MethodBase>();
                }
                catch { return Enumerable.Empty<MethodBase>(); }
            })
            .Where(m => m.GetCustomAttribute<I1ReadFailureMayWriteAttribute>() != null)
            .ToList();

        declared.Should().NotBeEmpty(
            "the backward check needs at least one declared exemption to have anything to prove the "
            + "analyser alive with. If the last one was legitimately removed, remove this assertion "
            + "with it and say so — do not leave a control that cannot fail.");

        // THE EXEMPTION MUST SAY SOMETHING. Measured by the cold gate 2026-09-12: with the reason
        // replaced by "", this census stayed GREEN for that offender, and the attribute's own doc
        // claimed the census "requires that one exists" when nothing read it at all. The C# compiler
        // forces an ARGUMENT; only this forces a REASON. An exemption nobody can explain is the
        // failure mode this repo tracks, so a bare or token reason must not be able to silence it.
        foreach (var m in declared)
        {
            var why = m.GetCustomAttribute<I1ReadFailureMayWriteAttribute>()!.Why;
            why.Should().NotBeNullOrWhiteSpace(
                $"{m.DeclaringType?.Name}.{m.Name} claims an I1 exemption without saying why. The "
                + "reason is the only thing standing between a declared exemption and a silent one.");
            why!.Trim().Length.Should().BeGreaterThan(40,
                $"{m.DeclaringType?.Name}.{m.Name}'s exemption reason is too short to name what the "
                + "write preserves and why it is not a built-in default landing on a customer's file. "
                + $"Got: \"{why.Trim()}\"");
        }

        var found = findings.Select(f => f.Method).ToHashSet();
        foreach (var m in declared)
            found.Should().Contain(m,
                $"{m.DeclaringType?.Name}.{m.Name} carries [I1ReadFailureMayWrite] but the analyser no "
                + "longer flags it. Either the method was fixed — remove the attribute — or the "
                + "analyser has gone blind, in which case every real offender is passing silently too.");
    }

    /// <summary>
    /// THE REACH PIN: the enumerator must still see the three stores whose omission cost an operator
    /// their data on 2026-08-04. Narrow the predicate in
    /// <see cref="I1ConfigStoreAnalyzer.ConfigStoreTypes"/> and this goes red, which is the point — the
    /// census's value is entirely in what it enumerates, and that is the one property a passing
    /// census cannot demonstrate about itself.
    /// </summary>
    [Theory]
    [InlineData("SQLTriage.Data.ServerConnectionManager")]
    [InlineData("SQLTriage.Data.Services.ScheduledTaskDefinitionService")]
    [InlineData("SQLTriage.Data.Services.AlertDefinitionService")]
    public void The_enumerator_reaches_the_stores_that_were_not_on_the_2026_08_04_list(string typeName)
    {
        var stores = I1ConfigStoreAnalyzer.ConfigStoreTypes(Product);
        _out.WriteLine($"config store types = {stores.Count}");
        stores.Should().Contain(typeName,
            "this store was invisible to the hand-kept adoption list on StoreWriteIntent and an "
            + "operator's data was destroyed through it. The whole reason this census reads the "
            + "compiled assembly is so membership is decided by the code rather than by memory.");
    }

    /// <summary>
    /// THE NEGATIVE CONTROL. A census that has never been seen to flag anything is not evidence that
    /// nothing is wrong — absence of evidence is evidence of absence only once the instrument is proved
    /// able to show a positive. <see cref="I1CensusNegativeControl"/> is compiled into THIS assembly in
    /// the exact shape of the defect the lane fixed, and is never invoked; the analyser is pointed at
    /// the test assembly and must find it.
    /// </summary>
    [Fact]
    public void The_census_can_show_a_positive()
    {
        var here = typeof(I1CensusNegativeControl).Assembly;

        I1ConfigStoreAnalyzer.ConfigStoreTypes(here)
            .Should().Contain(typeof(I1CensusNegativeControl).FullName!,
                "the control builds a path under Config\\ and writes a file, so Part A must classify it "
                + "as a config store before Part B can ever look at it");

        var findings = I1ConfigStoreAnalyzer.FailedReadReachesReplacingWrite(here);
        var hit = findings.Where(f => f.DisplayName.StartsWith(
            typeof(I1CensusNegativeControl).FullName + ".", StringComparison.Ordinal)).ToList();

        foreach (var f in hit) _out.WriteLine("  " + f);
        hit.Should().NotBeEmpty(
            "the control reads inside a try, swallows the failure, and then falls out of the handler "
            + "into File.WriteAllText — which is ReportPageConfigService.Load's exact shape, the write "
            + "sitting AFTER the try/catch rather than inside it. If the analyser cannot see this it "
            + "cannot see that either, and every green run of the core pin means nothing.");
    }

    /// <summary>Prints the census for the record. Asserts only that it is non-trivial, so a broken
    /// enumerator cannot leave the gate reading an empty list as a clean estate.
    ///
    /// <para><b>THE ONE TEST IN THIS CLASS THAT PINS A NUMBER MEASURED OFF THE PRODUCT ASSEMBLY, so
    /// it is the one that refuses a build configuration that number was not frozen in.</b> The 20 is
    /// a floor under a count taken from product IL on 2026-09-12, and MEASURED 2026-09-16 at
    /// <c>7b997b4</c> a count taken off IL is configuration-specific: the S-1 census reads 120
    /// command sites in Release and 117 in Debug at the identical commit, because Roslyn merges
    /// hoisted async state-machine fields when optimisations are ON. THIS enumerator happens not to
    /// move — measured 30 store types in Release-full AND Debug-full, 29 in both community cells, 3
    /// findings in all four — but "it did not move on this week's codegen" is not a record of what it
    /// was measured in, and the next change to the enumerator has no such measurement. The guard
    /// fails LOUDLY and its repair is one flag.</para>
    ///
    /// <para>The other facts in this class deliberately do NOT call it: they assert SETS (every
    /// finding declared, every declared method still found, a named store present) and a prose-length
    /// floor, none of which is a frozen magnitude, and all of which pass in both configurations —
    /// measured 6/6 green in all four cells. A guard demanded where it is meaningless is a guard that
    /// gets deleted.</para></summary>
    [Fact]
    public void Census_is_recorded_for_the_gate()
    {
        ProductAssemblyBuildConfiguration.RequireTheBuildConfigurationTheseNumbersWereFrozenIn(
            Product, _out, "the floor of 20 under the config-store type count, frozen by measurement "
            + "at 30 types on 2026-09-12 and re-measured at 30 (full) / 29 (community) on 2026-09-16");

        var stores = I1ConfigStoreAnalyzer.ConfigStoreTypes(Product);
        var findings = I1ConfigStoreAnalyzer.FailedReadReachesReplacingWrite(Product);

        _out.WriteLine($"CONFIG_STORE_TYPES={stores.Count}");
        foreach (var s in stores) _out.WriteLine("  " + s);
        _out.WriteLine($"READ_FAILURE_REACHES_REPLACING_WRITE={findings.Count}");
        foreach (var f in findings)
            _out.WriteLine($"  {f}  declared={f.Method.GetCustomAttribute<I1ReadFailureMayWriteAttribute>() != null}");

        stores.Count.Should().BeGreaterThan(20,
            "this product had 30 config-store types when the census was written on 2026-09-12. A "
            + "collapse to a handful means the enumerator stopped resolving IL, not that the stores "
            + "went away — and a census that enumerates nothing passes everything.");
    }
}

/// <summary>
/// ⚠ NEVER INVOKED. Compiled solely so <see cref="ConfigStoreI1CensusTests.The_census_can_show_a_positive"/>
/// has a known positive to detect, in the exact shape of the defect fixed in
/// <c>ReportPageConfigService.Load</c> on 2026-09-12: read inside a <c>try</c>, swallow in the
/// <c>catch</c>, then fall out of the handler into a replacing write of built-in defaults.
///
/// <para>Calling this would create a file. Nothing does, and nothing should: its only purpose is its
/// IL. The path is built under the system temp folder rather than the install so that even a mistaken
/// call could not touch a real config store.</para>
/// </summary>
internal sealed class I1CensusNegativeControl
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "Config", "i1-census-negative-control.json");

    internal string LoadShapedLikeTheDefect()
    {
        try
        {
            var json = File.ReadAllText(_path);
            if (!string.IsNullOrWhiteSpace(json)) return json;
        }
        catch (Exception)
        {
            // Swallowed, exactly as the defect did.
        }

        var defaults = "{\"version\":1}";
        File.WriteAllText(_path, defaults);
        return defaults;
    }
}
