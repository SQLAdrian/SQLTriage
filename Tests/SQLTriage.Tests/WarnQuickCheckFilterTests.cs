/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// B2 (2026-07-20) — the QuickCheck audit grid's status filter.
    ///
    /// <para>Two defects, one seam. (1) The <c>"passed"</c> filter predicate was
    /// <c>r.Passed &amp;&amp; !IsSkipped &amp;&amp; !IsInfoVerdict &amp;&amp; no error</c> — missing the
    /// <c>!IsWarnVerdict</c> guard that <c>PassedCount</c> already had. The dropdown therefore
    /// promised "Passed (N)" and the list rendered N rows PLUS every WARN row: a client filtering
    /// to passes was shown checks that could not assess the server. (2) The <c>"warn"</c> case in
    /// the filter switch worked correctly but no <c>&lt;option&gt;</c> ever selected it, so the one
    /// view that showed WARN honestly was unreachable from the UI.</para>
    ///
    /// <para>The invariant that outlives both: <b>the count in a filter's label and the rows that
    /// filter returns must be computed by the same predicate</b>, and <b>every filter case must be
    /// reachable</b>.</para>
    /// </summary>
    public class WarnQuickCheckFilterTests
    {
        private static readonly Type QuickCheckType =
            typeof(QuickCheckStateService).Assembly.GetType("SQLTriage.Pages.QuickCheck")!;

        private static CheckResult Pass(string id) => new()
        {
            CheckId = id, CheckName = id, Category = "Security", Severity = "High", Passed = true,
        };

        /// <summary>A corpus WARN as CheckExecutionService produces one: Passed=true, Verdict=WARN,
        /// no ErrorMessage.</summary>
        private static CheckResult Warn(string id) => new()
        {
            CheckId = id, CheckName = id, Category = "Security", Severity = "High",
            Passed = true, Verdict = "WARN",
            Message = "Could not read 3 of 14 databases (insufficient permissions).",
        };

        private static CheckResult Info(string id) => new()
        {
            CheckId = id, CheckName = id, Category = "Security", Severity = "INFO", Passed = true,
        };

        private static CheckResult Fail(string id) => new()
        {
            CheckId = id, CheckName = id, Category = "Security", Severity = "High", Passed = false,
        };

        /// <summary>Builds a real QuickCheck component with a real state service holding
        /// <paramref name="results"/>, sets the status filter, and returns what the grid renders.</summary>
        private static List<CheckResult> FilteredFor(string filter, params CheckResult[] results)
        {
            var component = Activator.CreateInstance(QuickCheckType)!;
            var state = new QuickCheckStateService();
            state.Results = results.ToList();

            // SelectedFilter moved to the SCOPED QuickCheckViewState on 2026-08-02: the estate's
            // results stay on the singleton, this circuit's filter does not.
            var view = new QuickCheckViewState { SelectedFilter = filter };

            QuickCheckType
                .GetProperty("State", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(component, state);
            QuickCheckType
                .GetProperty("View", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(component, view);

            var filtered = (IEnumerable<CheckResult>)QuickCheckType
                .GetProperty("FilteredResults", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(component)!;

            return filtered.ToList();
        }

        private static int CountProperty(string name, params CheckResult[] results)
        {
            var component = Activator.CreateInstance(QuickCheckType)!;
            var state = new QuickCheckStateService();
            state.Results = results.ToList();

            QuickCheckType
                .GetProperty("State", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(component, state);
            QuickCheckType
                .GetProperty("View", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(component, new QuickCheckViewState());

            return (int)QuickCheckType
                .GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(component)!;
        }

        [Fact]
        public void PassedFilter_DoesNotListWarnRows()
        {
            var rows = new[] { Pass("p1"), Pass("p2"), Warn("w1"), Info("i1"), Fail("f1") };

            var listed = FilteredFor("passed", rows);

            listed.Should().NotContain(r => r.CheckId == "w1",
                "a WARN could not assess the server — listing it under Passed shows a client a pass that was never asserted");
            listed.Select(r => r.CheckId).Should().BeEquivalentTo(new[] { "p1", "p2" });
        }

        [Fact]
        public void PassedFilterAndPassedCount_AgreeExactly()
        {
            // The header says "Passed (N)". The list must be those same N rows — this is the
            // property that was violated, and it is stronger than either half alone.
            var rows = new[] { Pass("p1"), Pass("p2"), Warn("w1"), Warn("w2"), Info("i1"), Fail("f1") };

            var listed = FilteredFor("passed", rows);
            var count = CountProperty("PassedCount", rows);

            listed.Count.Should().Be(count,
                "the label's count and the filtered list must be computed by the same predicate");
            count.Should().Be(2);
        }

        [Fact]
        public void WarnFilter_ReturnsExactlyTheWarnRows()
        {
            var rows = new[] { Pass("p1"), Warn("w1"), Warn("w2"), Info("i1"), Fail("f1") };

            // 2026-07-21: the filter token is "partial" — the label, the chip and the DOM value
            // now spell the state one way. WARN remains the corpus verdict, not a UI name.
            var listed = FilteredFor("partial", rows);

            listed.Select(r => r.CheckId).Should().BeEquivalentTo(new[] { "w1", "w2" });
            listed.Count.Should().Be(CountProperty("WarnCount", rows));
        }

        // ── the dropdown must actually be able to select every filter the switch implements ──

        [Fact]
        public void EveryFilterCase_IsReachableFromTheStatusDropdown()
        {
            // The defect was a WORKING filter case with no option to select it. Asserted against
            // the real shipped markup (QuickCheck.razor is copied to the test output by
            // SQLTriage.Tests.csproj) rather than a comment claiming the option exists.
            var markup = ReadQuickCheckMarkup();

            var switchBody = ExtractFilterSwitch(markup);
            var cases = Regex.Matches(switchBody, @"""(?<name>[a-z]+)""\s*=>")
                             .Select(m => m.Groups["name"].Value)
                             .Distinct()
                             .ToList();

            cases.Should().Contain("partial", "the Partial filter case is the one this test exists for");
            cases.Count.Should().BeGreaterThan(4, "the filter switch should have been found, not an empty match");

            var options = Regex.Matches(markup, @"<option value=""(?<name>[a-z]*)""")
                               .Select(m => m.Groups["name"].Value)
                               .Distinct()
                               .ToList();

            foreach (var c in cases)
            {
                options.Should().Contain(c,
                    $"the '{c}' filter case is implemented but must also be selectable from the Status dropdown");
            }
        }

        [Fact]
        public void TheWarnOption_IsLabelledConsistentlyWithTheCardAndBadge()
        {
            // One vocabulary for one concept: the summary card, the row badge and the dropdown all
            // say "Partial". A third name for the same tier would read as a third state.
            // 2026-07-21: the option's VALUE is now "partial" too. It was "warn", which is exactly
            // the third name this test's own rationale forbids — the label read Partial, the chip
            // read PARTIAL, and the DOM value read warn. The corpus verdict token stays WARN; the
            // UI vocabulary is "partial" end to end.
            var markup = ReadQuickCheckMarkup();

            markup.Should().MatchRegex(@"<option value=""partial"">Partial \(@WarnCount\)</option>",
                "the dropdown option must exist, be labelled Partial, carry a matching value, and show the same count the card does");
            markup.Should().NotContain(@"value=""warn""",
                "the UI must not carry a second spelling of the Partial state");
        }

        private static string ReadQuickCheckMarkup()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", "QuickCheck.razor");
            File.Exists(path).Should().BeTrue(
                "QuickCheck.razor is copied to the test output by SQLTriage.Tests.csproj; " +
                "if this fails the assertions below would vacuously pass");
            return File.ReadAllText(path);
        }

        /// <summary>Slices out just the FilteredResults status switch, so the case scan cannot pick
        /// up unrelated switch expressions elsewhere in the component.</summary>
        private static string ExtractFilterSwitch(string markup)
        {
            var start = markup.IndexOf("View.SelectedFilter switch", StringComparison.Ordinal);
            start.Should().BeGreaterThan(-1, "the filter switch must be findable for this test to mean anything");
            var end = markup.IndexOf("};", start, StringComparison.Ordinal);
            end.Should().BeGreaterThan(start);
            return markup.Substring(start, end - start);
        }
    }
}
