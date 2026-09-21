/* In the name of God, the Merciful, the Compassionate */

// lane9-04 (2026-08-28) - the pages lane's own live harnesses, and why they are pinned here.
//
// THE DEFECT. Seven of this lane's new "live" tests reported PASSED while measuring nothing.
// SessionKillLiveTests (3), BestPracticeRunLiveTests (3) and AgentJobReadLiveTests (1) each used a
// plain [Fact] with an arming early-return:
//
//     [Fact]
//     public async Task Something_live()
//     {
//         if (string.IsNullOrWhiteSpace(Target)) { _out.WriteLine("UNARMED: ..."); return; }
//
// Unarmed, that is `Total tests: 7, Passed: 7` - seven assertion-free green ticks folded into every
// later "suite green" claim. It is the exact class this wave exists to close, committed inside the
// wave itself, and it was inconsistent within the lane: VaCoverageHonestyLiveTests already used
// LiveFactAttribute and correctly reported SKIPPED.
//
// WHY A LANE-LOCAL TEST RATHER THAN THE WHOLE-TREE CENSUS. Portal/LiveHarnessArmingCensusTests
// walks every .cs file for this shape and is the standing instrument. It did not catch these seven
// because its match needs `GetEnvironmentVariable(` and a bare `return;` within six lines of each
// other, and these harnesses read the variable through a `Target` PROPERTY defined far from the
// guard. Widening that scanner's pattern is the move that has been defeated repeatedly in this
// repo - it moves the boundary to the next shape rather than removing it - so the gap is recorded
// as a residual for a lane that can drive the change tree-wide, and THESE files are pinned exactly.

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace SQLTriage.Tests;

public class PagesLaneLiveHarnessArmingTests
{
    /// <summary>The pages lane's live harnesses, by filename. Named, not globbed: a new harness in
    /// this lane is a deliberate addition and should be added here deliberately too.</summary>
    private static readonly string[] LaneHarnesses =
    {
        "SessionKillLiveTests.cs",
        "BestPracticeRunLiveTests.cs",
        "AgentJobReadLiveTests.cs",
        "VaCoverageHonestyLiveTests.cs",
    };

    /// <summary>
    /// The three files that carried the defect, and in which EVERY test needs an instance - so a
    /// plain <c>[Fact]</c> in one of them is always the early-return shape coming back.
    ///
    /// <para>VaCoverageHonestyLiveTests is not on this list because it legitimately holds one
    /// offline <c>[Fact]</c> (<c>A_whitespace_server_name_yields_no_target_at_all</c>: a whitespace
    /// ServerNames yields no target, which needs no instance). The rule being enforced is "a test
    /// that needs an instance must not run unarmed", not "no [Fact] may live in a file with Live in
    /// its name".</para>
    /// </summary>
    private static readonly string[] AllTestsNeedAnInstance =
    {
        "SessionKillLiveTests.cs",
        "BestPracticeRunLiveTests.cs",
        "AgentJobReadLiveTests.cs",
    };

    private static string TestsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("this reads harness SOURCE, so it needs the repo root");
        return Path.Combine(dir!.FullName, "Tests", "SQLTriage.Tests");
    }

    /// <summary>Source with <c>//</c> and <c>///</c> comment lines dropped, so a lint about what
    /// the code DOES is not answered by what its comments SAY.</summary>
    private static string CodeLines(string source) =>
        string.Join("\n", source.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Source(string file)
    {
        var path = Path.Combine(TestsRoot(), file);
        File.Exists(path).Should().BeTrue(
            $"{file} is one of this lane's live harnesses; a rename must update LaneHarnesses, or "
            + "every assertion below would vacuously pass");
        return File.ReadAllText(path);
    }

    [Fact]
    public void No_pages_lane_live_harness_uses_a_plain_Fact()
    {
        // [Fact] runs unarmed and is therefore counted as a pass. [LiveFact(...)] computes Skip at
        // discovery time and is reported as SKIPPED.
        // Code lines only. These files explain the defect they closed by naming the attribute that
        // caused it, and a whole-file search reads that explanation as the defect - the trap this
        // repo's own markup lints already carry a helper for.
        var offenders = AllTestsNeedAnInstance
            .Where(f => CodeLines(Source(f)).Contains("[Fact]", StringComparison.Ordinal))
            .ToList();

        offenders.Should().BeEmpty(
            "an unarmed live harness must SKIP (LiveFact) or FAIL (an assertion on its arming "
            + "variable), never return quietly and be counted green. Offenders: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_pages_lane_live_harness_declares_LiveFact_and_asserts_its_own_arming()
    {
        foreach (var file in LaneHarnesses)
        {
            var source = Source(file);

            source.Should().Contain("[LiveFact(", $"{file} must skip rather than pass when unarmed");

            // The attribute is not the whole guard (LiveFactAttribute's own doc says so): if it is
            // ever weakened or removed, each body must FAIL on its arming variable rather than run
            // past an empty target.
            source.Should().Contain("RequireTarget",
                $"{file} must assert its arming variable inside the test body as well");
        }
    }

    [Fact]
    public void No_pages_lane_live_harness_still_carries_the_arming_early_return()
    {
        // The exact source shape that produced seven green ticks for zero measurement. Matched on
        // the guard rather than on the environment read, which is precisely the half the whole-tree
        // census cannot see when the read sits behind a property.
        foreach (var file in AllTestsNeedAnInstance)
        {
            var source = CodeLines(Source(file));
            source.Should().NotContain("UNARMED:",
                $"{file}'s early-return printed this and returned green");
            System.Text.RegularExpressions.Regex
                .IsMatch(source, @"if \(string\.IsNullOrWhiteSpace\(Target\)\)")
                .Should().BeFalse($"{file} must not branch on its target and return");
        }
    }
}
