/* In the name of God, the Merciful, the Compassionate */

// WHAT THE ROADMAP'S ARTEFACTS SAY, honesty-hunt ledger lane 5 (Clusters B, C, E, F).
//
// One run produced three artefacts - the client PDF, the Action Plan CSV, and the screen - and they
// disagreed with each other and with their own labels. The defects here are all of one family: a
// printed claim that no measurement in the run supports.
//
//   CLUSTER B, contradictions between artefacts. The level status rule was written twice and the
//   two copies could print opposite verdicts for the same level of the same run, both stamped with
//   one run id (audit-r1-09). The cover's progress dots read one variable as "achieved through"
//   while the summary badge read the same variable as "working on", so a server failing Foundation
//   outright still printed a filled Foundation dot (audit-r2-05). The severity banding in the code
//   and the banding in the comment above it named different levels and different weights
//   (audit-r2-08). About 180 lines of a second, self-contradicting report sat in the page behind
//   display:none, long after the browser-print path that rendered it retired (audit-r2-07).
//
//   CLUSTER C, fabricated or wrong-field PDF content. The Operator Attestation claimed the report
//   came from SQLTriage's own check corpus when it is built entirely from sp_Blitz / sp_triage CSV
//   output (audit-r1-03). It attested a compliance-framework string that was a one-line return
//   whose own doc comment claimed it was read from a file that has no such field (audit-r1-04). The
//   fix slot under every failed check was bound to business_translation, which is why-it-matters
//   prose, not the fix (audit-r2-06). The CSV's "Recommended Action" column was a six-way switch on
//   the category, so two unrelated checks in one category emitted byte-identical advice
//   (audit-r1-10).
//
//   CLUSTER E, execution bookkeeping. A CSV export was named from a stamp captured when the runner
//   was CONSTRUCTED, so two exports in one process resolved to one file and the second destroyed
//   the first with no log line (audit-r1-01). A run that skipped the EXEC and exported the previous
//   execution's rows logged "executed successfully" and raised nothing in the post-run modal
//   (audit-r1-05, ruled real/low).
//
//   CLUSTER F, the dashboard depth label. "Share of fired exceptions with full corpus analysis" was
//   rendered from a ratio over the whole catalogue: different numerator, different denominator,
//   different meaning. And the "health cannot be scored" banner could render beside a "weighted
//   health 100%" tooltip, because an empty catalogue scored 100 (audit-r1-11).
//
// WHAT THE INSTRUMENTS HERE ARE, honestly labelled. RoadmapReportRules, BlitzDashboardService and
// DiagnosticScriptRunner.NextFreeExportPath are exercised for real. Pages/DiagnosticsRoadmap.razor,
// Pages/FullAudit.razor and Data/Services/RoadmapPdfBuilder.cs hold rules inside a Blazor component
// or a QuestPDF composition, and nothing in CI renders either, so those are held by TEXT TRIPWIRES -
// the same instrument and the same admitted limit as AuditUniverseAndParseHonestyTests. A tripwire
// proves the call site. It does not prove the pixels.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class RoadmapArtefactHonestyTests
    {
        private static string RoadmapRazor() => File.ReadAllText(
            Path.Combine(FrkContractTests.RepoRoot(), "Pages", "DiagnosticsRoadmap.razor"));

        private static string PdfBuilder() => File.ReadAllText(
            Path.Combine(FrkContractTests.RepoRoot(), "Data", "Services", "RoadmapPdfBuilder.cs"));

        private static string FullAuditRazor() => File.ReadAllText(
            Path.Combine(FrkContractTests.RepoRoot(), "Pages", "FullAudit.razor"));

        private static string ScriptRunner() => File.ReadAllText(
            Path.Combine(FrkContractTests.RepoRoot(), "Data", "DiagnosticScriptRunner.cs"));

        // ── Cluster B: one status rule ──────────────────────────────────────────────────────

        [Fact]
        public void The_level_status_contradiction_in_the_filed_scenario_is_gone()
        {
            // audit-r1-09's own numbers: {L1 95, L2 70, L3 88, L4 85, L5 92}, target 80.
            var rates = new[] { 95, 70, 88, 85, 92 };
            var levels = rates.Select(r => (PassedCount: r, TotalCount: 100,
                                            TargetPercent: RoadmapReportRules.DefaultTargetPercent)).ToList();

            RoadmapReportRules.AchievedLevel(levels).Should().Be(1,
                "L2 is under target, so the ladder stops at L1 no matter what L3 to L5 scored");

            // The ladder position and the level's own measurement are DIFFERENT questions. The CSV
            // used to answer the second question with the first, and that is the contradiction: by
            // ladder position L5 is three levels above the current stage and read NOT STARTED, while
            // the PDF measured its 92% and read COMPLETE. One run, one id, two verdicts.
            RoadmapReportRules.LevelStatus(rates[4], 100).Should().Be(RoadmapReportRules.StatusTargetMet,
                "L5 passed 92 of 100 checks against a target of 80. Any artefact saying otherwise is "
                + "contradicting the pass rate it prints on the same row");

            RoadmapReportRules.LevelStatus(rates[1], 100).Should().Be(RoadmapReportRules.StatusBelowTarget,
                "L2 passed 70 of 100 against a target of 80");
        }

        [Fact]
        public void Both_artefacts_read_the_one_status_rule_and_neither_keeps_a_literal()
        {
            var razor = RoadmapRazor();
            var pdf = PdfBuilder();

            razor.Should().Contain("RoadmapReportRules.LevelStatus(",
                "the Action Plan CSV must take its status from the shared rule");
            pdf.Should().Contain("RoadmapReportRules.LevelStatus(",
                "and so must the PDF's Executive Summary table");

            foreach (var dead in new[] { "\"NOT STARTED\"", "\"IN PROGRESS\"", "\"COMPLETE\"" })
            {
                razor.Should().NotContain(dead,
                    "a hand-written status literal is how the two rules drifted apart; the words now "
                    + "live in RoadmapReportRules and nowhere else");
                pdf.Should().NotContain(dead, "same rule, same reason, other artefact");
            }
        }

        [Fact]
        public void A_level_with_no_checks_is_not_a_level_that_passed()
        {
            // The two copies of the ladder disagreed on exactly this case: the per-server one read an
            // empty level as 100% and passed it, the main one read it as 0% and stopped there. Same
            // data, two maturity levels, on two surfaces of one report. It stopped being theoretical
            // the day a universe filter started removing ids from the map.
            RoadmapReportRules.TargetMet(0, 0).Should().BeFalse(
                "no checks means no evidence, and no evidence is not a pass");
            RoadmapReportRules.LevelStatus(0, 0).Should().Be(RoadmapReportRules.StatusNotAssessed,
                "and it must say which of the two it is, rather than reading as a measured failure");

            var withEmptyL2 = new List<(int, int, int)>
            {
                (10, 10, 80),   // L1 met
                (0, 0, 80),     // L2 has no checks at all
                (10, 10, 80),   // L3 would be met
            };
            RoadmapReportRules.AchievedLevel(withEmptyL2).Should().Be(1,
                "the ladder cannot climb through a level nothing was measured at");
        }

        [Fact]
        public void The_per_server_level_and_the_overall_level_come_from_one_ladder()
        {
            var razor = RoadmapRazor();

            var computeLevel = Between(razor,
                "private static (int Level, double Score) ComputeLevel(", "// ── Load check map from");
            computeLevel.Should().NotBeEmpty("ComputeLevel must still be findable by name");
            computeLevel.Should().Contain("RoadmapReportRules.AchievedLevel(",
                "the per-server table used its own copy of the ladder, and the copies disagreed");
            computeLevel.Should().NotContain("rate >= 80",
                "an inline threshold here is the second copy growing back");
        }

        // ── Cluster B: the cover dots (audit-r2-05) ─────────────────────────────────────────

        [Fact]
        public void A_server_that_fails_foundation_has_achieved_nothing()
        {
            var failsL1 = new List<(int, int, int)> { (0, 10, 80), (10, 10, 80) };

            RoadmapReportRules.AchievedLevel(failsL1).Should().Be(0,
                "0% at Foundation is zero dots filled. The dots used to fill from the current stage, "
                + "which is floored at 1, so Foundation printed as reached at any pass rate");
            RoadmapReportRules.CurrentStage(0).Should().Be(1,
                "the stage being worked on is still L1, and that is what the badge means");
        }

        [Fact]
        public void The_pdf_cover_dots_fill_from_the_achieved_level_not_the_current_stage()
        {
            var pdf = PdfBuilder();
            pdf.Should().Contain("var reached = lv <= achieved;",
                "the dots read 'achieved through'. Reading OverallLevel there is audit-r2-05");
            pdf.Should().Contain("RoadmapReportRules.AchievedLevel(",
                "and 'achieved' has to be the shared rule, not a second copy of the ladder");
        }

        // The type-level half of this fact lives in
        // Gated/RoadmapPdfBuilderTests.The_report_carries_no_AchievedLevel_field_for_a_caller_to_set:
        // it binds RoadmapReport, which the community build removes, so it cannot compile here.

        [Fact]
        public void A_report_whose_levels_were_never_assessed_fills_no_dots()
        {
            // The fabricated shape, measured through the shared rule the builder now calls. The PDF
            // itself is rendered by RoadmapPdfBuilderTests; nothing in this project extracts text
            // from a PDF, so this asserts the number the cover reads and not the glyph it paints.
            var neverAssessed = Enumerable.Range(1, 5).Select(_ => (0, 0, 80)).ToList();

            RoadmapReportRules.AchievedLevel(neverAssessed).Should().Be(0,
                "no level was assessed, so no level was achieved, and the cover must print five "
                + "hollow dots beside the five NOT ASSESSED rows");
        }

        // ── Cluster B: severity banding (audit-r2-08) ───────────────────────────────────────

        [Theory]
        [InlineData(1, "Critical", 4)]
        [InlineData(2, "Critical", 4)]
        [InlineData(3, "High", 2)]
        [InlineData(4, "Medium", 1)]
        [InlineData(5, "Medium", 1)]
        public void The_severity_band_and_weight_are_one_rule(int level, string band, int weight)
        {
            RoadmapReportRules.SeverityBand(level).Should().Be(band);
            RoadmapReportRules.RiskWeight(level).Should().Be(weight);
        }

        [Fact]
        public void The_caption_printed_beside_the_counts_describes_the_banding_that_produced_them()
        {
            // The whole defect was a description that did not match the code it described. So the
            // description is now checked against the code, not against a reviewer's attention.
            var caption = RoadmapReportRules.SeverityBandCaption;

            foreach (var level in Enumerable.Range(1, 5))
            {
                var band = RoadmapReportRules.SeverityBand(level);
                var sentence = caption.Split('.').FirstOrDefault(s => s.TrimStart().StartsWith(band));
                sentence.Should().NotBeNull("the caption must name the {0} band", band);
                sentence!.Should().Contain("Level " + level,
                    "the caption says {0} counts certain levels, and level {1} is banded {0} by the "
                    + "code. A caption that omits it is the comment-versus-code split all over again",
                    band, level);
            }

            RoadmapRazor().Should().Contain("RoadmapReportRules.SeverityBand(lvl)",
                "the page's counters must bucket by the same rule the caption describes");
            PdfBuilder().Should().Contain("RoadmapReportRules.SeverityBandCaption",
                "and the client PDF must print the caption beside the counts");
        }

        // ── Cluster B: the dead second report (audit-r2-07) ─────────────────────────────────

        [Fact]
        public void The_second_self_contradicting_report_is_gone_from_the_page()
        {
            var razor = RoadmapRazor();

            foreach (var block in new[] { "pdf-cover-page", "print-executive-summary", "pdf-attestation-page" })
                razor.Should().NotContain(block,
                    "{0} was print-only markup for a browser-print path that retired. Nothing set "
                    + "body.printing-active any more, so it had not rendered in some time, and it had "
                    + "drifted into a second report that contradicted the QuestPDF one", block);

            razor.Should().NotContain("its own diagnostic check corpus",
                "the dead attestation claimed corpus provenance in one paragraph and raw sp_Blitz "
                + "output in the next");

            var css = File.ReadAllText(Path.Combine(
                FrkContractTests.RepoRoot(), "wwwroot", "css", "DiagnosticsRoadmap.css"));
            css.Should().NotContain("#roadmap-print-region",
                "the rule that hid the dead block goes with the block; a selector for markup that "
                + "does not exist is the next reader's false lead");
        }

        // ── Cluster C: provenance and the attestation (audit-r1-03, audit-r1-04) ────────────

        [Fact]
        public void The_operator_attestation_states_the_provenance_the_data_actually_has()
        {
            var pdf = PdfBuilder();

            pdf.Should().NotContain("from the results of its own diagnostic check corpus",
                "the roadmap is built from AuditOutputScanner's read of sp_Blitz / sp_triage CSVs, "
                + "mapped by BlitzCheckID. A 2026-07-22 'correction' replaced one false provenance "
                + "claim with its opposite, inside the section headed Operator Attestation");

            pdf.Should().Contain("from sp_Blitz or sp_triage output collected from the servers",
                "say where the findings came from");
            pdf.Should().Contain("did not re-run those checks",
                "and say what the tool did not do, because the reader cannot tell from the output");
        }

        [Fact]
        public void The_attested_framework_row_is_no_longer_a_hard_coded_compliance_claim()
        {
            var razor = RoadmapRazor();
            var pdf = PdfBuilder();

            razor.Should().NotContain("ResolveFrameworkVersion",
                "a one-line return whose doc comment claimed it was read from roadmap-mapping.json, "
                + "which exposes no such field, printed as an attested compliance claim");
            razor.Should().NotContain("\"ISO 27001:2022 · SOC 2 · NIST CSF 2.0\"",
                "the literal it returned");

            razor.Should().Contain("ControlMappingSets",
                "the names now come from the same list the Compliance Mapping panel renders");
            razor.Should().Contain("@ControlMappingSets[0]",
                "and the panel headers read that list, so adding or removing a mapping moves both");

            pdf.Should().Contain("AttRow(table, \"Control mappings\", r.ControlMappings);",
                "the row is labelled for what it is: a property of the build");
            pdf.Should().Contain("It is not an assessment against those frameworks",
                "and the page says so in words, next to the row");
        }

        // ── Cluster C: the fix slot (audit-r2-06, audit-r1-10) ──────────────────────────────

        [Fact]
        public void Impact_and_action_are_two_fields_in_two_labelled_slots()
        {
            var pdf = PdfBuilder();

            pdf.Should().NotContain("public string? Remediation;",
                "business_translation was bound to a property called Remediation and printed, "
                + "unlabelled, where a reader looks for the fix");
            pdf.Should().Contain("public string? Impact;");
            pdf.Should().Contain("public string? Action;");
            pdf.Should().Contain("t.Span(\"Impact  \")", "the why-it-matters line is labelled");
            pdf.Should().Contain("t.Span(\"Action  \")", "and the fix line is labelled and separate");

            RoadmapRazor().Should().Contain("Action      = _nextAction.GetValueOrDefault(f.CheckId),",
                "next_action is the mapping's per-check fix and was not read at all");
        }

        [Fact]
        public void An_unresolved_template_placeholder_is_not_shippable_advice()
        {
            RoadmapReportRules.PerCheckAction("Engage <Owner> to action this.").Should().BeNull(
                "template scaffolding in a client PDF is not a recommendation");
            RoadmapReportRules.PerCheckAction("  ").Should().BeNull();
            RoadmapReportRules.PerCheckAction(null).Should().BeNull();

            RoadmapReportRules.PerCheckAction("Read the extended property\tand confirm it.")
                .Should().Be("Read the extended property and confirm it.",
                    "clean text survives, with the source's tab separators flattened for a cell");
        }

        [Fact]
        public void Nothing_the_mapping_offers_as_an_action_reaches_a_report_with_a_placeholder_in_it()
        {
            // The property, over the shipped data. It stays true when the mapping is refilled, which
            // a census of "18 populated, 14 with placeholders" would not.
            var json = File.ReadAllText(FrkContractTests.ConfigPath("roadmap-mapping.json"));
            using var doc = JsonDocument.Parse(json);
            var entries = doc.RootElement.GetProperty("blitzCheckMap").EnumerateArray().ToList();

            var offered = entries
                .Where(e => e.TryGetProperty("next_action", out var na)
                            && !string.IsNullOrWhiteSpace(na.GetString()))
                .ToList();
            offered.Should().NotBeEmpty(
                "if the field empties entirely, the two-column CSV and the PDF Action line are "
                + "carrying nothing and this test should say so rather than pass vacuously");

            foreach (var e in offered)
            {
                var resolved = RoadmapReportRules.PerCheckAction(e.GetProperty("next_action").GetString());
                if (resolved is null) continue;
                RoadmapReportRules.HasUnresolvedPlaceholder(resolved).Should().BeFalse(
                    "CheckID {0} would print '{1}' to a client",
                    e.GetProperty("checkId").GetInt32(), resolved);
            }
        }

        [Fact]
        public void The_category_sentence_no_longer_wears_the_per_check_column_header()
        {
            var razor = RoadmapRazor();

            razor.Should().NotContain("GetRecommendedAction",
                "the six-way switch on the category tag is category guidance, and it shipped under a "
                + "header reading Recommended Action, so CheckID 1 (no backups) and CheckID 68 (no "
                + "CHECKDB) emitted byte-identical per-check advice");
            razor.Should().Contain("GetCategoryGuidance",
                "renamed to what it is");
            razor.Should().Contain("Check-Specific Action,Category Guidance,Why It Matters",
                "three columns, three different things, each named");
        }

        // ── Cluster E: the export stamp (audit-r1-01) ───────────────────────────────────────

        [Fact]
        public void A_second_export_never_destroys_the_first()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sqlt-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var wanted = Path.Combine(dir, "SERVER_sp_Blitz_20260826_120000.csv");

                DiagnosticScriptRunner.NextFreeExportPath(wanted, out var firstCollided)
                    .Should().Be(wanted, "nothing is there yet");
                firstCollided.Should().BeFalse();

                File.WriteAllText(wanted, "run one");
                var second = DiagnosticScriptRunner.NextFreeExportPath(wanted, out var secondCollided);
                secondCollided.Should().BeTrue("the caller has to be able to log that this happened");
                second.Should().NotBe(wanted);
                File.Exists(second).Should().BeFalse("the returned path must be free to write");

                File.WriteAllText(second, "run two");
                File.ReadAllText(wanted).Should().Be("run one",
                    "run one was silently destroyed by run two before this fix: two exports in one "
                    + "process produced ONE file, and the survivor held run two");

                var third = DiagnosticScriptRunner.NextFreeExportPath(wanted, out _);
                third.Should().NotBe(wanted).And.NotBe(second);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void The_export_name_is_stamped_when_the_export_happens_not_when_the_runner_is_built()
        {
            var runner = ScriptRunner();

            runner.Should().NotContain("FiletimeStamp",
                "a field initialised at construction stamped every export a runner instance ever made "
                + "with the time it was BUILT. A long-lived host makes that worse the longer it runs");
            runner.Should().Contain("{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                "the stamp belongs to the export");
            runner.Should().Contain("NextFreeExportPath(csvFileName, out var collided)",
                "and a same-second collision still must not overwrite");
        }

        // ── Cluster E: the reused run (audit-r1-05) ─────────────────────────────────────────

        [Fact]
        public void A_run_that_skipped_the_exec_says_so_in_the_log_and_in_the_modal()
        {
            var runner = ScriptRunner();

            runner.Should().Contain("result.ReusedPreviousExecution = true;",
                "the skip path must record what kind of run this was as a fact, not as a label string");
            runner.Should().Contain("reused the PREVIOUS execution's rows",
                "the success line asserted the script 'executed successfully' when no EXEC ran");
            runner.Should().Contain("if (result.ReusedPreviousExecution)",
                "and the two log lines have to be chosen by that fact");

            var full = FullAuditRazor();
            full.Should().Contain("if (r.ReusedPreviousExecution)",
                "the post-run modal raised nothing on this path: Success true, no error, rows returned");
            full.Should().Contain("No check ran on this server.",
                "so the operator was never told the results were not this run's");
            full.Should().Contain("\"Reused\"",
                "and it is its own severity, because this is not a fault");
        }

        [Fact]
        public void The_reuse_notice_does_not_claim_the_dates_are_wrong()
        {
            // The filing framed this as stale data presented as fresh. It is not: the exported CSV
            // carries the real, older CheckDate and both readers take the audit date from that
            // column. Ruled real/low, log line and modal only. The copy must not overstate it.
            var full = FullAuditRazor();
            full.Should().Contain("keep their own original", "the rows carry their true CheckDate");
            full.Should().Contain("the audit date shown is the true one",
                "say the part that is fine, or the operator re-runs an audit that did not need it");
        }

        // ── Cluster F: the dashboard depth label (audit-r1-11) ──────────────────────────────

        [Fact]
        public void Depth_measures_the_fired_set_the_label_names()
        {
            // Two corpus-backed ids and two map-only ids in the catalogue; one of each fires.
            var corpus = new[] { BlitzCheck("01", "1"), BlitzCheck("02", "2") };
            var mapJson = """
            { "blitzCheckMap": [
              { "checkId": 3, "findingName": "Map three", "category": "Reliability", "IsBad": 1 },
              { "checkId": 4, "findingName": "Map four",  "category": "Reliability", "IsBad": 1 }
            ] }
            """;
            var catalog = BlitzDashboardService.BuildCatalog(corpus, mapJson);

            var file = Fired("S", 1, 3);
            var report = BlitzDashboardService.ComputeInstanceReport(file, catalog);

            report.AnalysisDepth.Should().Be(0.5,
                "two exceptions fired and one of them carries corpus-backed analysis. The old "
                + "numerator counted CATALOGUE entries and the denominator was the whole scored "
                + "universe plus unclassified fires: a different numerator, a different denominator, "
                + "and a different meaning from the sentence rendered beside it");

            BlitzDashboardService.ComputeInstanceReport(Fired("S"), catalog).AnalysisDepth
                .Should().Be(0, "nothing fired, so there is no share of the fired set to report");
        }

        [Fact]
        public void The_dashboard_label_and_the_number_name_one_population()
        {
            var razor = File.ReadAllText(
                Path.Combine(FrkContractTests.RepoRoot(), "Pages", "BlitzDashboard.razor"));

            razor.Should().NotContain("Share of fired exceptions with full corpus analysis\">",
                "the old title claimed a fired-set share and was fed a catalogue ratio");
            razor.Should().Contain("of exceptions fully analysed",
                "the visible label has to name the same population the number measures");
            razor.Should().Contain("no exceptions to analyse",
                "and 0% is the wrong thing to print when nothing fired");
        }

        [Fact]
        public void An_instance_with_no_catalogue_to_score_against_is_not_a_perfect_instance()
        {
            var empty = new Dictionary<int, BlitzCatalogEntry>();

            // Both shapes: nothing fired, which is the 100% one, and a nameless fire, which used to
            // score 0% off a universe of one unclassified id.
            BlitzDashboardService.ComputeInstanceReport(Fired("S", 1), empty).Scorable
                .Should().BeFalse("a fired id nobody can name is not a universe to score against");

            var report = BlitzDashboardService.ComputeInstanceReport(Fired("S"), empty);

            report.Scorable.Should().BeFalse(
                "the page renders a 'health cannot be scored' banner when the catalogue is "
                + "unavailable, and the same data used to feed a 'weighted health 100%' tooltip "
                + "beside it. Both rendered off one empty universe");
            report.UnscorableReason.Should().Contain("catalogue is unavailable");
            report.HealthScore.Should().Be(0, "no claim is being made");

            BlitzDashboardService.EstateScore(new[] { report }).Should().Be(0.0,
                "and the estate rollup must not average in an instance nothing was measured on");
        }

        // ── The two findings this lane closed by verification, not by fixing ────────────────

        [Fact]
        public void The_roadmap_and_the_dashboard_still_share_one_audit_parser()
        {
            // audit-r1-06, fixed by the exportpack lane on 2026-08-24 before this lane opened.
            // Pinned here so the dedup cannot silently come apart again.
            var razor = RoadmapRazor();
            razor.Should().Contain("@inject IAuditOutputScanner AuditScanner",
                "the page carried its own copy of the enum, the scan and the fired-CheckID parse "
                + "until 2026-08-24; three implementations of one rule is how the sp_Blitz CSV shape "
                + "drifted out from under two of them");
            razor.Should().NotContain("enum AuditFileType",
                "one declaration, in AuditOutputScanner");
            razor.Should().Contain("AuditScanner.LoadFiredChecksAsync(file)",
                "and the page must actually call it");
        }

        [Fact]
        public void The_shipped_sp_Blitz_output_query_names_its_CheckDate_column()
        {
            // audit-r1-08, also closed by the exportpack lane. The old query selected an unaliased
            // CONVERT(VARCHAR, CheckDate, 120), so the column arrived nameless and had to be found by
            // position, and it prepended a second ServerName column the readers could not resolve.
            var configJson = File.ReadAllText(FrkContractTests.ConfigPath("script-configurations.json"));
            using var doc = JsonDocument.Parse(configJson);

            var blitz = doc.RootElement.EnumerateArray()
                .First(e => e.GetProperty("Name").GetString() == "sp_Blitz");
            var outputQuery = blitz.GetProperty("SqlQueryForOutput").GetString() ?? "";

            outputQuery.Should().Contain("AS CheckDate",
                "the column has to arrive with a name, or the reader is back to counting columns");
            outputQuery.Should().NotContain("xp_regread",
                "the Domain column that came with it is gone too");

            var scanner = File.ReadAllText(Path.Combine(
                FrkContractTests.RepoRoot(), "Data", "Services", "AuditOutputScanner.cs"));
            scanner.Should().Contain("IndexOf(headers, \"CheckDate\")",
                "and the reader has to resolve it BY NAME to get the benefit");
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        private static SqlCheck BlitzCheck(string suffix, string source) => new()
        {
            Id = "SQLT-BLITZ-" + suffix,
            Name = "Corpus check " + suffix,
            Source = source,
            Category = "Reliability",
            Severity = "High",
            ScoreWeight = 5,
            IsBad = true,
        };

        private static AuditedFile Fired(string instance, params int[] ids)
        {
            var file = new AuditedFile
            {
                SqlInstance = instance,
                Domain = "D",
                AuditDate = new DateTime(2026, 8, 26),
                FileType = AuditFileType.SpBlitz,
                FilePath = instance + ".csv",
            };
            foreach (var id in ids) file.FiredCheckCounts[id] = 1;
            return file;
        }

        private static string Between(string haystack, string start, string end)
        {
            var a = haystack.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return "";
            var b = haystack.IndexOf(end, a, StringComparison.Ordinal);
            return b < 0 ? haystack[a..] : haystack[a..b];
        }
    }
}
