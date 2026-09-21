/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Worklist 22c — Classification-integrity verification harness for
    /// Config/roadmap-mapping.json. Loads the REAL shipped file and
    /// asserts structural + content invariants on every blitzCheckMap entry.
    ///
    /// Schema discovered 2026-05-17:
    ///   checkId              int    — join key (must be unique, &gt;0)
    ///   blitzId              string — e.g. "BLITZ_1"
    ///   findingName          string — human label
    ///   category             string — tag (derived canonical set)
    ///   Roadmap              string — remediation/roadmap area
    ///   level                int    — maturity level 1..5 (per-entry authority)
    ///   IsBad                int    — 0 or 1; MUST be explicitly present (no silent default)
    ///   business_translation string — plain-language explanation (must be non-empty)
    ///
    /// Each [Fact] collects ALL violations before failing, so one test run
    /// reveals the full set of data debt rather than stopping at first failure.
    /// </summary>
    public class RoadmapMappingClassificationTests
    {
        // ── File resolution ───────────────────────────────────────────────
        // Mirrors the production pattern in DiagnosticsRoadmap.razor and BuildCatalogueService:
        // AppContext.BaseDirectory (the test bin dir) already has Config/ copied by the
        // main project's content-copy pipeline (verified by prior build inspection).
        private static readonly string MappingFilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "roadmap-mapping.json");

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Loads and parses the blitzCheckMap array once. Returns a list of
        /// JsonElement (one per entry) plus the raw doc for IsBad presence checks.
        /// </summary>
        private static (JsonDocument Doc, List<JsonElement> Entries) LoadEntries()
        {
            Assert.True(File.Exists(MappingFilePath),
                $"roadmap-mapping.json not found at {MappingFilePath}. " +
                "Build the test project first so Config/ is copied to bin.");

            var json = File.ReadAllText(MappingFilePath);
            var doc = JsonDocument.Parse(json);

            Assert.True(
                doc.RootElement.TryGetProperty("blitzCheckMap", out var arr),
                "roadmap-mapping.json root object has no 'blitzCheckMap' property.");

            var entries = arr.EnumerateArray().ToList();
            Assert.NotEmpty(entries);
            return (doc, entries);
        }

        private static string CheckLabel(JsonElement entry)
        {
            var id = entry.TryGetProperty("checkId", out var cid) ? cid.ToString() : "?";
            var name = entry.TryGetProperty("findingName", out var fn) ? fn.GetString() ?? "" : "";
            return $"checkId={id} ({name})";
        }

        // ── Rule 1: level is present and 1..5 ─────────────────────────────
        [Fact]
        public void Rule1_AllEntries_LevelIsPresentAndInRange1To5()
        {
            var (_, entries) = LoadEntries();
            var violations = new List<string>();

            foreach (var entry in entries)
            {
                var label = CheckLabel(entry);
                if (!entry.TryGetProperty("level", out var lvlProp))
                {
                    violations.Add($"  MISSING level — {label}");
                    continue;
                }
                if (lvlProp.ValueKind != JsonValueKind.Number || !lvlProp.TryGetInt32(out var lvl))
                {
                    violations.Add($"  level not an integer — {label} value={lvlProp}");
                    continue;
                }
                if (lvl < 1 || lvl > 5)
                    violations.Add($"  level={lvl} out of range 1..5 — {label}");
            }

            Assert.True(violations.Count == 0,
                BuildMessage("Rule 1 (level 1..5)", violations, entries.Count));
        }

        // ── Rule 2: business_translation is present and non-whitespace ────
        [Fact]
        public void Rule2_AllEntries_BusinessTranslationIsPresentAndNonEmpty()
        {
            var (_, entries) = LoadEntries();
            var violations = new List<string>();

            foreach (var entry in entries)
            {
                var label = CheckLabel(entry);
                if (!entry.TryGetProperty("business_translation", out var bt))
                {
                    violations.Add($"  MISSING business_translation — {label}");
                    continue;
                }
                var text = bt.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(text))
                    violations.Add($"  business_translation is empty/whitespace — {label}");
            }

            Assert.True(violations.Count == 0,
                BuildMessage("Rule 2 (business_translation present+non-empty)", violations, entries.Count));
        }

        // ── Rule 3: category is present and non-empty ──────────────────────
        // The canonical set is derived from the data itself (all distinct non-null/non-empty
        // values). We assert:
        //   (a) every entry has a non-null, non-whitespace category, AND
        //   (b) no entry uses a category that is null or empty (no additional enum constraint
        //       because the categoryLevelMap diverges from the actual entry set by design).
        [Fact]
        public void Rule3_AllEntries_CategoryIsPresentAndNonEmpty()
        {
            var (_, entries) = LoadEntries();
            var violations = new List<string>();
            var distinctValues = new HashSet<string>();

            foreach (var entry in entries)
            {
                var label = CheckLabel(entry);
                if (!entry.TryGetProperty("category", out var cat))
                {
                    violations.Add($"  MISSING category — {label}");
                    continue;
                }
                var val = cat.GetString();
                if (string.IsNullOrWhiteSpace(val))
                    violations.Add($"  category is null/empty — {label}");
                else
                    distinctValues.Add(val);
            }

            // Report the canonical set in the failure message if there are violations,
            // so the developer can see what values ARE in use.
            Assert.True(violations.Count == 0,
                BuildMessage(
                    $"Rule 3 (category non-empty; canonical set in-file: [{string.Join(", ", distinctValues.OrderBy(x => x))}])",
                    violations, entries.Count));
        }

        // ── Rule 4: IsBad is EXPLICITLY present in every entry ────────────
        // Production code defaults a missing IsBad to true (fail).
        // That silent default is a classification gap: an author who forgets IsBad=0
        // accidentally causes the check to penalise maturity. No silent defaults.
        [Fact]
        public void Rule4_AllEntries_IsBadIsExplicitlyPresent()
        {
            var (_, entries) = LoadEntries();
            var violations = new List<string>();

            foreach (var entry in entries)
            {
                var label = CheckLabel(entry);
                if (!entry.TryGetProperty("IsBad", out var ib))
                {
                    violations.Add($"  MISSING IsBad (production silently defaults to 1=fail) — {label}");
                    continue;
                }
                if (ib.ValueKind != JsonValueKind.Number || !ib.TryGetInt32(out var ibVal))
                {
                    violations.Add($"  IsBad is not an integer — {label} value={ib}");
                    continue;
                }
                if (ibVal != 0 && ibVal != 1)
                    violations.Add($"  IsBad={ibVal} is not 0 or 1 — {label}");
            }

            Assert.True(violations.Count == 0,
                BuildMessage("Rule 4 (IsBad explicitly 0 or 1)", violations, entries.Count));
        }

        // ── Rule 5: blitzId is present+non-empty and checkId is unique ────
        [Fact]
        public void Rule5_AllEntries_BlitzIdNonEmptyAndCheckIdUnique()
        {
            var (_, entries) = LoadEntries();
            var violations = new List<string>();
            var seenCheckIds = new Dictionary<int, string>(); // checkId → first label

            foreach (var entry in entries)
            {
                var label = CheckLabel(entry);

                // checkId
                if (!entry.TryGetProperty("checkId", out var cidProp))
                {
                    violations.Add($"  MISSING checkId — {label}");
                }
                else if (cidProp.ValueKind != JsonValueKind.Number || !cidProp.TryGetInt32(out var cid))
                {
                    violations.Add($"  checkId is not an integer — {label}");
                }
                else if (seenCheckIds.TryGetValue(cid, out var firstLabel))
                {
                    violations.Add($"  DUPLICATE checkId={cid} — second occurrence at {label}; first at {firstLabel}");
                }
                else
                {
                    seenCheckIds[cid] = label;
                }

                // blitzId
                if (!entry.TryGetProperty("blitzId", out var blitz))
                    violations.Add($"  MISSING blitzId — {label}");
                else if (string.IsNullOrWhiteSpace(blitz.GetString()))
                    violations.Add($"  blitzId is empty/whitespace — {label}");
            }

            Assert.True(violations.Count == 0,
                BuildMessage("Rule 5 (blitzId non-empty + checkId unique)", violations, entries.Count));
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static string BuildMessage(string ruleName, List<string> violations, int total)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{ruleName}] {violations.Count}/{total} entries failed:");
            foreach (var v in violations)
                sb.AppendLine(v);
            return sb.ToString();
        }
    }
}
