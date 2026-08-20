/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Models;
using SQLTriage.Data.Parser;
using SQLTriage.Data.Services.FrameworkTree;
using Xunit;
using FrameworkMapping = SQLTriage.Data.Parser.FrameworkMapping;

namespace SQLTriage.Tests
{
    public class FrameworkTreeServiceTests
    {
        // ───────────────────────── splitters: each known grammar ─────────────────────────

        [Theory]
        // NIST SP 800-53 + FedRAMP: dash family, base then enhancement.
        [InlineData("NIST SP 800-53 Rev 5", "CM-6", "CM|CM-6")]
        [InlineData("NIST SP 800-53 Rev 5", "CP-9(2)", "CP|CP-9|CP-9(2)")]
        [InlineData("FedRAMP High", "AC-2(1)", "AC|AC-2|AC-2(1)")]
        // NIST CSF 2.0: function -> category -> subcategory.
        [InlineData("NIST CSF 2.0", "PR.PS-05", "PR|PR.PS|PR.PS-05")]
        // CMMC 2.0: domain -> level -> full (800-171 ref never split).
        [InlineData("CMMC 2.0", "AC.L2-3.1.1", "AC|AC.L2|AC.L2-3.1.1")]
        // HIPAA: dotted base whole, then each paren group one cumulative level.
        [InlineData("HIPAA Security Rule", "164.308(a)(7)(ii)(A)", "164.308|164.308(a)|164.308(a)(7)|164.308(a)(7)(ii)|164.308(a)(7)(ii)(A)")]
        // SOX ITGC via COBIT: letters -> letters+num1 -> full.
        [InlineData("SOX ITGC (via COBIT 2019)", "BAI10.02", "BAI|BAI10|BAI10.02")]
        // Essential Eight: maturity level -> full.
        [InlineData("Essential Eight (ACSC)", "ML1-P7", "ML1|ML1-P7")]
        // SOC 2: family (letters+digits) -> full.
        [InlineData("SOC 2 (2017 TSC)", "CC6.1", "CC6|CC6.1")]
        // Cyber Essentials: single flat node.
        [InlineData("Cyber Essentials", "Firewalls", "Firewalls")]
        // NIST 800-171: generic dotted (routed away from dash family by the 800-171 guard).
        [InlineData("NIST SP 800-171 Rev 3", "3.1.1", "3|3.1|3.1.1")]
        // ISO / PCI / generic dotted, incl. trailing paren as one leaf level.
        [InlineData("ISO/IEC 27001:2022", "8.13", "8|8.13")]
        [InlineData("PCI-DSS v4.0", "8.3.6", "8|8.3|8.3.6")]
        // Unknown / future framework -> generic dotted (zero code changes).
        [InlineData("Totally Made Up Framework v9 (2027)", "A.8.2", "A|A.8|A.8.2")]
        [InlineData("Totally Made Up Framework v9 (2027)", "2.15", "2|2.15")]
        [InlineData("Totally Made Up Framework v9 (2027)", "8.13(a)", "8|8.13|8.13(a)")]
        [InlineData("Totally Made Up Framework v9 (2027)", "SINGLE", "SINGLE")]
        public void ComputeLevels_matches_expected_grammar(string framework, string controlId, string expectedPipe)
        {
            var levels = FrameworkControlSplitter.ComputeLevels(framework, controlId, out var fallback);
            Assert.False(fallback);
            Assert.Equal(expectedPipe.Split('|'), levels.ToArray());
        }

        // ───────────────────────── GDPR: the exact demo defect ─────────────────────────
        // The original non-cumulative spec (Article 5 -> 5.1 -> (e)) dropped every
        // control_name because no node's full_id ever equalled the literal control_id.
        // The fix makes the chain cumulative so the leaf equals the verbatim control_id.

        [Theory]
        [InlineData("GDPR (EU 2016/679)", "Article 32.1(c)", "Article 32|Article 32.1|Article 32.1(c)")]
        [InlineData("GDPR (EU 2016/679)", "Article 5.1(e)", "Article 5|Article 5.1|Article 5.1(e)")]
        [InlineData("GDPR (EU 2016/679)", "Article 5", "Article 5")]
        [InlineData("GDPR (EU 2016/679)", "32.1(c)", "32|32.1|32.1(c)")]
        public void ComputeLevels_gdpr_is_cumulative_leaf_equals_controlId(string framework, string controlId, string expectedPipe)
        {
            var levels = FrameworkControlSplitter.ComputeLevels(framework, controlId, out var fallback);
            Assert.False(fallback);
            Assert.Equal(expectedPipe.Split('|'), levels.ToArray());
            // The LAW: the leaf full_id equals the verbatim control_id.
            Assert.Equal(controlId, levels[^1]);
        }

        // ───────────────── property: cumulative full-ids + leaf == control_id ─────────────────

        public static IEnumerable<object[]> LawSamples() => new[]
        {
            new object[] { "NIST SP 800-53 Rev 5", "CP-9(2)" },
            new object[] { "FedRAMP Moderate", "AC-2" },
            new object[] { "NIST CSF 2.0", "PR.PS-05" },
            new object[] { "CMMC 2.0", "AC.L2-3.1.1" },
            new object[] { "HIPAA Security Rule", "164.308(a)(7)" },
            new object[] { "GDPR (EU 2016/679)", "Article 32.1(c)" },
            new object[] { "SOX ITGC (via COBIT 2019)", "BAI10.02" },
            new object[] { "Essential Eight (ACSC)", "ML1-P7" },
            new object[] { "SOC 2 (2017 TSC)", "CC6.1" },
            new object[] { "ISO/IEC 27017:2015", "12.4.1" },
            new object[] { "Cyber Essentials", "Malware protection" },
            new object[] { "Some Future Framework 2030", "X.9.4(b)" },
        };

        [Theory]
        [MemberData(nameof(LawSamples))]
        public void ComputeLevels_law_leaf_equals_controlId_and_ids_are_cumulative(string framework, string controlId)
        {
            var levels = FrameworkControlSplitter.ComputeLevels(framework, controlId, out _);
            Assert.NotEmpty(levels);
            // leaf equals verbatim control_id (name-attachment fires only on this equality).
            Assert.Equal(controlId, levels[^1]);
            // every child's full_id extends its parent's (cumulative).
            for (var i = 1; i < levels.Count; i++)
                Assert.StartsWith(levels[i - 1], levels[i], StringComparison.Ordinal);
        }

        [Fact]
        public void ComputeLevels_falls_back_flat_when_shape_does_not_fit_family()
        {
            // A known dash-family framework given an id that does not fit the dash shape
            // falls back to a single flat node and flags it.
            var levels = FrameworkControlSplitter.ComputeLevels("NIST SP 800-53 Rev 5", "not a control", out var fallback);
            Assert.True(fallback);
            Assert.Equal(new[] { "not a control" }, levels.ToArray());
        }

        // ───────────────────────── majority-vote naming ─────────────────────────

        [Fact]
        public void BuildForest_leaf_takes_majority_vote_and_intermediate_takes_its_family_title()
        {
            var checks = new List<SqlCheck>
            {
                Check("C1", "Backup enabled", "High"),
                Check("C2", "Backup verified", "Medium"),
                Check("C3", "Backup retention", "Low"),
            };
            var mappings = Map(
                ("C1", "ISO/IEC 27001:2022", "5.1", "Policies for information security"),
                ("C2", "ISO/IEC 27001:2022", "5.1", "Policies for information security"),
                ("C3", "ISO/IEC 27001:2022", "5.1", "Security policy")); // minority variant

            var forest = FrameworkTreeService.BuildForest(checks, mappings);
            var iso = forest.Frameworks.Single(f => f.Name == "ISO/IEC 27001:2022");

            // Intermediate node "5" is never a mapped control_id, so it has no vote. It used to
            // render completely bare (the L1/L2 "no name" defect); it now falls back to the
            // published ISO 27001:2022 Annex A theme title. Critically it is NOT the child's
            // name — "Policies for information security" names 5.1, not the whole theme.
            var five = iso.Roots.Single(r => r.FullId == "5");
            Assert.Equal("Organizational Controls", five.Name);
            Assert.NotEqual("Policies for information security", five.Name);

            // leaf "5.1" carries the majority-vote control_name (2 vs 1).
            var leaf = five.Children.Single(c => c.FullId == "5.1");
            Assert.Equal("Policies for information security", leaf.Name);
            Assert.Equal(3, leaf.Checks.Count);
            Assert.Equal(3, leaf.CheckCount);
        }

        // ───────────────────────── numeric-aware sibling sort ─────────────────────────

        [Fact]
        public void BuildForest_sorts_siblings_numerically_2_before_10()
        {
            var checks = new List<SqlCheck> { Check("C1", "t", "Low"), Check("C2", "t", "Low"), Check("C3", "t", "Low") };
            var mappings = Map(
                ("C1", "ISO/IEC 27001:2022", "10.1", "Ten"),
                ("C2", "ISO/IEC 27001:2022", "2.1", "Two"),
                ("C3", "ISO/IEC 27001:2022", "9.1", "Nine"));

            var forest = FrameworkTreeService.BuildForest(checks, mappings);
            var iso = forest.Frameworks.Single(f => f.Name == "ISO/IEC 27001:2022");
            Assert.Equal(new[] { "2", "9", "10" }, iso.Roots.Select(r => r.FullId).ToArray());
        }

        // ───────────────────────── worst-of rollup precedence (R4) ─────────────────────────

        [Theory]
        [InlineData(FrameworkVerdict.Fail, FrameworkVerdict.Warn, FrameworkVerdict.Fail)]
        [InlineData(FrameworkVerdict.Warn, FrameworkVerdict.Pass, FrameworkVerdict.Warn)]
        [InlineData(FrameworkVerdict.Pass, FrameworkVerdict.Skip, FrameworkVerdict.Pass)]
        [InlineData(FrameworkVerdict.Skip, FrameworkVerdict.Info, FrameworkVerdict.Skip)]
        [InlineData(FrameworkVerdict.Info, FrameworkVerdict.None, FrameworkVerdict.Info)]
        public void Worse_follows_precedence_fail_warn_pass_skip_info(FrameworkVerdict a, FrameworkVerdict b, FrameworkVerdict expected)
        {
            Assert.Equal(expected, FrameworkTreeService.Worse(a, b));
            Assert.Equal(expected, FrameworkTreeService.Worse(b, a)); // symmetric
        }

        [Theory]
        [InlineData("FAIL", false, FrameworkVerdict.Fail)]
        [InlineData("warn", true, FrameworkVerdict.Warn)]
        [InlineData("Pass", true, FrameworkVerdict.Pass)]
        [InlineData("SKIP", true, FrameworkVerdict.Skip)]
        [InlineData("INFO", true, FrameworkVerdict.Info)]
        [InlineData(null, true, FrameworkVerdict.Pass)]   // numeric-contract, passed
        [InlineData(null, false, FrameworkVerdict.Fail)]  // numeric-contract / errored, not passed
        public void ParseVerdict_maps_raw_five_states_with_passed_fallback(string? verdict, bool passed, FrameworkVerdict expected)
        {
            var r = new CheckResult { CheckId = "X", Verdict = verdict, Passed = passed };
            Assert.Equal(expected, FrameworkTreeService.ParseVerdict(r));
        }

        [Fact]
        public void ParseVerdict_fallback_excludes_nonScorable_tiers_that_ride_Passed()
        {
            // A verdict-less result that is really SKIP or INFO carries Passed=true. The fallback
            // must route it through CheckClassification, not read Passed raw, or a non-assertion
            // renders as a green Pass on the compliance tree (the raw-.Passed false-clean class).
            var infoBySeverity = new CheckResult { CheckId = "I", Verdict = null, Severity = "INFO", Passed = true };
            Assert.Equal(FrameworkVerdict.Info, FrameworkTreeService.ParseVerdict(infoBySeverity));

            var skipByMessage = new CheckResult { CheckId = "S1", Verdict = null, Message = "SKIP — not applicable", Passed = true };
            Assert.Equal(FrameworkVerdict.Skip, FrameworkTreeService.ParseVerdict(skipByMessage));

            var skipByError = new CheckResult { CheckId = "S2", Verdict = null, ErrorMessage = "permission denied", Passed = false };
            Assert.Equal(FrameworkVerdict.Skip, FrameworkTreeService.ParseVerdict(skipByError));

            // A genuinely scorable numeric-contract pass still passes — no false demotion.
            var scorablePass = new CheckResult { CheckId = "P", Verdict = null, Severity = "High", Message = "ok", Passed = true };
            Assert.Equal(FrameworkVerdict.Pass, FrameworkTreeService.ParseVerdict(scorablePass));
        }

        // ───────────────────────── join: verdicts attach by CheckId ─────────────────────────

        [Fact]
        public void Join_attaches_verdicts_by_checkId_and_ignores_and_counts_unknown_ids()
        {
            var checks = new List<SqlCheck> { Check("C1", "t", "High"), Check("C2", "t", "Medium") };
            var mappings = Map(
                ("C1", "ISO/IEC 27001:2022", "5.1", "Policies"),
                ("C2", "ISO/IEC 27001:2022", "5.2", "Roles"));
            var forest = FrameworkTreeService.BuildForest(checks, mappings);

            var results = new List<CheckResult>
            {
                new() { CheckId = "C1", Verdict = "FAIL", Passed = false },
                new() { CheckId = "C2", Verdict = "PASS", Passed = true },
                new() { CheckId = "C1", Verdict = "WARN", Passed = true }, // second server, worse-of applies
                new() { CheckId = "GHOST", Verdict = "PASS", Passed = true }, // maps to no control
            };

            var byId = FrameworkTreeService.IndexResults(results);
            Assert.Equal(FrameworkVerdict.Fail, byId["C1"]); // FAIL worse than WARN
            Assert.Equal(FrameworkVerdict.Pass, byId["C2"]);

            var iso = forest.Frameworks.Single(f => f.Name == "ISO/IEC 27001:2022");
            var root = iso.Roots.Single(r => r.FullId == "5");
            // root rolls up the worst of its two controls (C1 FAIL, C2 PASS) -> FAIL.
            Assert.Equal(FrameworkVerdict.Fail, FrameworkTreeService.RollupNode(root, byId));

            // GHOST attaches to no control: ignored from the tree, counted as unknown.
            Assert.Equal(1, FrameworkTreeService.CountUnknownResultIds(forest, results));
        }

        [Fact]
        public void BuildForest_lists_unmapped_checks_as_honest_denominator()
        {
            var checks = new List<SqlCheck>
            {
                Check("C1", "Mapped", "High"),
                Check("C2", "Orphan", "Low"),
            };
            var mappings = Map(("C1", "ISO/IEC 27001:2022", "5.1", "Policies"));

            var forest = FrameworkTreeService.BuildForest(checks, mappings);
            Assert.Equal(2, forest.TotalChecks);
            Assert.Equal(1, forest.MappedCheckCount);
            var orphan = Assert.Single(forest.UnmappedChecks);
            Assert.Equal("C2", orphan.CheckId);
        }

        // ───────────────────────── helpers ─────────────────────────

        private static SqlCheck Check(string id, string name, string severity) =>
            new() { Id = id, Name = name, Severity = severity };

        private static IReadOnlyDictionary<string, IReadOnlyList<FrameworkMapping>> Map(
            params (string CheckId, string Framework, string ControlId, string ControlName)[] entries)
        {
            var dict = new Dictionary<string, List<FrameworkMapping>>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                if (!dict.TryGetValue(e.CheckId, out var list)) { list = new(); dict[e.CheckId] = list; }
                list.Add(new FrameworkMapping(
                    e.Framework, e.ControlId, e.ControlName, "supports",
                    new Dictionary<string, string>()));
            }
            return dict.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<FrameworkMapping>)kv.Value,
                StringComparer.Ordinal);
        }
    }
}
