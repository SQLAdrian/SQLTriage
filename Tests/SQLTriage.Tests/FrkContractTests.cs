/* In the name of God, the Merciful, the Compassionate */

// THE FIRST RESPONDER KIT <-> APP CONTRACT, PINNED.
//
// Nothing in this repo pinned the FRK version, the set of CheckIDs sp_Blitz emits, the
// Finding strings roadmap-aliases.json keys itself on, or the fact that 8.34 sp_Blitz
// cannot run without sp_ineachdb. Every one of those is a string contract between a
// third-party script we vendor verbatim and app code that reads its output, and every one
// of them broke silently on the 8.28 -> 8.34 bump (the alias key "Acclerated Database
// Recovery Enabled" was upstream's own typo, fixed on 20260313).
//
// That is the house defect class: prose (a mapping file, a scoring universe) gated by
// different evidence than the thing it describes. These tests close it by structure: the
// expected values are checked in here, and a future FRK bump that changes any of them turns
// this file red instead of quietly changing what a client is told.
//
// WHAT THIS FILE DOES NOT DO. It reads the two scripts as TEXT. It does not run them. The
// live proof (install, EXEC, read the output table, build the report) is
// FrkLiveSmokeTests.cs, which is SKIPPED unless FRK_LIVE_TARGET names an instance.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    public class FrkContractTests
    {
        // -- The pin. Bump these two constants and this whole file becomes the checklist. --

        public const string ExpectedFrkVersion = "8.34";
        public const string ExpectedFrkVersionDate = "20260702";

        /// <summary>The upstream release TAG the vendored files came from (not main).</summary>
        public const string UpstreamTag = "20260708";

        // -- Locating the REPO sources, never a build-output copy -------------------------
        //
        // The test assembly's own output carries a scripts\ folder copied by the csproj, and it
        // is only as fresh as the last build. Anchoring on SQLTriage.sln makes the resolution
        // unambiguous, the same reason DashboardCounterArithmeticTests.ConfigPath does it.

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
                    return dir.FullName;
            }
            throw new InvalidOperationException(
                "Could not locate the repo root (SQLTriage.sln) above " + AppContext.BaseDirectory);
        }

        internal static string ScriptPath(string fileName)
        {
            var path = Path.Combine(RepoRoot(), "scripts", fileName);
            Assert.True(File.Exists(path), "Expected the vendored script at " + path);
            return path;
        }

        internal static string ConfigPath(string fileName)
        {
            var path = Path.Combine(RepoRoot(), "Config", fileName);
            Assert.True(File.Exists(path), "Expected the config file at " + path);
            return path;
        }

        private static string ScriptText(string fileName) => File.ReadAllText(ScriptPath(fileName));

        // ---------------------------------------------------------------------------------
        // (a) VERSION STAMP
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData("sp_Blitz.sql")]
        [InlineData("sp_ineachdb.sql")]
        public void Vendored_script_carries_the_pinned_frk_version_stamp(string fileName)
        {
            var text = ScriptText(fileName);

            // Upstream writes it as one assignment, with whitespace that differs between the two
            // files, so match the pair rather than a literal substring.
            var m = Regex.Match(
                text,
                @"@Version\s*=\s*'(?<v>[^']*)'\s*,\s*@VersionDate\s*=\s*'(?<d>[^']*)'",
                RegexOptions.IgnoreCase);

            Assert.True(m.Success,
                fileName + " has no @Version / @VersionDate assignment at all. Either the vendored "
                + "file is not a First Responder Kit script, or upstream changed how it stamps itself.");

            Assert.Equal(ExpectedFrkVersion, m.Groups["v"].Value);
            Assert.Equal(ExpectedFrkVersionDate, m.Groups["d"].Value);
        }

        [Fact]
        public void Vendored_scripts_keep_the_upstream_MIT_notice()
        {
            // The kit is MIT and the notice must survive in copies. It lives INSIDE sp_Blitz.sql.
            var text = ScriptText("sp_Blitz.sql");
            Assert.Contains("MIT License", text);
            Assert.Contains("Brent Ozar Unlimited", text);
        }

        // ---------------------------------------------------------------------------------
        // (b) ALIAS KEYS ARE sp_Blitz FINDING STRINGS
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The alias keys that are NOT verbatim substrings of sp_Blitz.sql, each with the reason.
        ///
        /// <para>The brief for this lane asked for "every key appears verbatim". Measured against
        /// 8.28 that was already false for four keys, so a bare every-key assertion would have gone
        /// red on arrival and taught the next reader to weaken it. Three of the four are Findings
        /// sp_Blitz COMPOSES at runtime from a column value, so the literal never appears in the
        /// source; the fourth has no traceable origin in this tree at all. They are pinned by name
        /// here, and <see cref="Every_exempted_alias_key_still_needs_its_exemption"/> makes the list
        /// shrink-only: the moment one of them starts appearing verbatim, the exemption fails.</para>
        /// </summary>
        private static readonly Dictionary<string, string> AliasKeysNotVerbatimInSpBlitz =
            new(StringComparer.Ordinal)
            {
                ["Poison Wait Detected: RESOURCE_SEMAPHORE"] =
                    "Composed at runtime: sp_Blitz.sql emits 'Poison Wait Detected: ' + wait_type.",
                ["Collation is Latin1_General_100_CI_AS_KS_WS"] =
                    "Composed at runtime: sp_Blitz.sql emits 'Collation is ' + collation_name.",
                ["Collation is Latin1_General_CI_AS"] =
                    "Composed at runtime: sp_Blitz.sql emits 'Collation is ' + collation_name.",
                ["Drive C Space"] =
                    "Origin untraced. It appears nowhere else in this repo, not in sp_Blitz.sql and "
                    + "not in SQLDBA.ORG.sp_triage.sql, and it sits in the file's own "
                    + "_comment_ignored block with a null value. Recorded, not guessed at.",
            };

        private static IReadOnlyList<string> AliasKeys()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath("roadmap-aliases.json")));
            return doc.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(n => !n.StartsWith("_", StringComparison.Ordinal))   // _comment, _comment_ignored
                .ToList();
        }

        [Fact]
        public void Every_alias_key_is_a_verbatim_finding_string_in_the_vendored_sp_Blitz()
        {
            var text = ScriptText("sp_Blitz.sql");
            var keys = AliasKeys();

            Assert.True(keys.Count > 40,
                "roadmap-aliases.json yielded only " + keys.Count + " keys. It carried 54 when this "
                + "test was written; a collapse that large means the file or the reader changed shape.");

            var missing = keys
                .Where(k => !text.Contains(k, StringComparison.Ordinal))
                .Where(k => !AliasKeysNotVerbatimInSpBlitz.ContainsKey(k))
                .ToList();

            Assert.True(missing.Count == 0,
                "These roadmap-aliases.json keys no longer appear verbatim in scripts/sp_Blitz.sql, so "
                + "they can never match a Finding the deployed script emits:\n  "
                + string.Join("\n  ", missing)
                + "\nRe-derive them from the new script, or add each to AliasKeysNotVerbatimInSpBlitz "
                + "with the reason it cannot be verbatim.");
        }

        [Fact]
        public void Every_exempted_alias_key_still_needs_its_exemption()
        {
            var text = ScriptText("sp_Blitz.sql");

            var noLongerNeeded = AliasKeysNotVerbatimInSpBlitz.Keys
                .Where(k => text.Contains(k, StringComparison.Ordinal))
                .ToList();

            Assert.True(noLongerNeeded.Count == 0,
                "These keys are exempted from the verbatim rule but DO now appear verbatim in "
                + "scripts/sp_Blitz.sql. Drop them from AliasKeysNotVerbatimInSpBlitz so the rule "
                + "covers them again:\n  " + string.Join("\n  ", noLongerNeeded));

            var stale = AliasKeysNotVerbatimInSpBlitz.Keys
                .Except(AliasKeys(), StringComparer.Ordinal)
                .ToList();
            Assert.True(stale.Count == 0,
                "These keys are exempted but no longer exist in roadmap-aliases.json: "
                + string.Join(", ", stale));
        }

        [Fact]
        public void The_typo_key_upstream_fixed_is_gone_and_its_replacement_is_present()
        {
            // The single key the 8.28 to 8.34 bump broke. Named explicitly, because a set-difference
            // failure message would not say WHY it mattered.
            var keys = AliasKeys();
            Assert.DoesNotContain("Acclerated Database Recovery Enabled", keys);
            Assert.Contains("Accelerated Database Recovery Enabled", keys);
        }

        // ---------------------------------------------------------------------------------
        // (c) THE EMITTED CheckID SET
        // ---------------------------------------------------------------------------------

        // SEVEN FORMS, because sp_Blitz assigns a CheckID in seven different syntactic places, and
        // the first version of this file knew two of them.
        //
        // ⚠⚠ HOW THAT WAS FOUND, 2026-08-23, and it is why this comment is long. The first cut used
        // forms A and B alone, produced a tidy 214-id list, and went green. The LIVE probe
        // (FrkLiveSmokeTests) then ran the script against a real instance, which emitted 65 distinct
        // ids, and SEVEN of them were absent from the 214: -1, 133, 156, 193, 230, plus the whole
        // 1001-1076 sp_configure family. A textual contract that passes while the thing it describes
        // does something else is the house defect class, and it was sitting inside the test written
        // to close that class. The forms below are what the measurement forced.

        /// <summary>
        /// FORM A: the INSERT ... SELECT literal, "273 AS CheckID".
        ///
        /// <para>THE TRAILING QUOTE IS LOAD-BEARING, added 2026-08-26. One row in the vendored 8.34
        /// script writes the id as a QUOTED STRING: <c>'199' AS CheckID</c>, the default-trace check.
        /// The original pattern required a bare integer, so 199 was absent from this extraction and
        /// therefore absent from <see cref="ExpectedCheckIds"/>, while the test named
        /// "emits_exactly" stayed green. That is the house defect class sitting inside the test
        /// written to close it.</para>
        ///
        /// <para>It also had to be fixed BEFORE anything in production consumed this derivation. The
        /// never-fires set in <c>CheckUniverse</c> is "the scoring universe minus what the script can
        /// emit", so a blind spot here does not merely under-report: it puts a LIVE check into the
        /// exclusion set and silently stops grading it. The score then moves in whichever direction
        /// that instance happens to make it move (up where the check fires, down where it passes),
        /// and on no instance does the number still measure the check. Corrected 2026-08-27: this
        /// passage read "raises every score", which is only the fires case.</para>
        /// </summary>
        private static readonly Regex CheckIdFormA =
            new(@"\b(\d+)'?\s+AS\s+CheckID\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// FORM B: the debug trace. Four spellings in one file, so the argument LIST is captured and
        /// every integer in it taken: "Running CheckId [%d].", "Running  CheckId [%d] and CheckId
        /// [%d]." (two ids, and note the double space), "Running CheckId [%d] through [%d] and [%d]
        /// through [%d]." (the ENDPOINTS of two ranges) and "Running check with id %d".
        /// </summary>
        private static readonly Regex CheckIdFormB =
            new(@"RAISERROR\s*\(\s*'Running\s+(?:check with id|CheckId)[^']*'\s*,\s*0\s*,\s*1\s*,\s*([0-9,\s]+?)\)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>FORM C: a literal-led INSERT INTO #BlitzResults ... VALUES ( -1 , ... ).</summary>
        private static readonly Regex CheckIdFormC =
            new(@"INSERT\s+INTO\s+\[?#BlitzResults\]?[^;]*?VALUES\s*\(\s*(-?\d+)\s*,",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>FORM D: a literal-led INSERT INTO #BlitzResults ... ) SELECT 156 , ...</summary>
        private static readonly Regex CheckIdFormD =
            new(@"INSERT\s+INTO\s+\[?#BlitzResults\]?[^;]*?\)\s*SELECT\s+(-?\d+)\s*,\s*[\r\n]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // Forms E, F and G are REGION-SCOPED: the id is found inside one INSERT statement for a named
        // temp table, never file-wide. That scoping is load-bearing. A file-wide version of form E
        // matched RAISERROR('...', 16, 0) and put a phantom CheckID 0 into the set.

        private static readonly Regex ConfigurationDefaultsRegion =
            new(@"INSERT\s+INTO\s+#ConfigurationDefaults(.*?);",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>FORM E, inside that region: ( 'max degree of parallelism', 0, 1032 ).</summary>
        private static readonly Regex ConfigurationDefaultsId =
            new(@"\(\s*'[^']*'\s*,\s*-?\d+\s*,\s*(\d+)\s*\)", RegexOptions.Compiled);

        private static readonly Regex DatabaseDefaultsRegion =
            new(@"INSERT\s+INTO\s+#DatabaseDefaults(.*?);",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// FORM F, inside that region. Anchored on the PRIORITY, which is 210 for all seventeen rows
        /// of this table, because the position before it is not always a literal: CheckID 133 has a
        /// CASE expression where its default value should be, and 133 is precisely the id the first
        /// extraction dropped.
        /// </summary>
        private static readonly Regex DatabaseDefaultsId =
            new(@"(\d+)\s*,\s*210\s*,", RegexOptions.Compiled);

        private static readonly Regex DatabaseScopedRegion =
            new(@"INSERT\s+INTO\s+#DatabaseScopedConfigurationDefaults(.*?);",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>FORM G, inside that region: ( 1, 'MAXDOP', '0', NULL, 194 ), trailing id.</summary>
        private static readonly Regex DatabaseScopedId =
            new(@",\s*(\d+)\s*\)", RegexOptions.Compiled);

        private static readonly Regex AnyInteger = new(@"-?\d+", RegexOptions.Compiled);

        /// <summary>
        /// The union of the seven forms.
        ///
        /// <para>KNOWN ARTEFACTS, measured rather than suspected. Read these before trusting a diff.</para>
        /// <list type="bullet">
        /// <item>It is a TEXTUAL upper bound on what CAN fire, not a runtime observation. An id behind
        /// a version gate no instance satisfies is still counted.</item>
        /// <item>It does NOT pick up ids that appear only inside a #SkipChecks guard, which is why 129
        /// and 157 read as retired at 8.34: they survive only inside
        /// "CheckID IN (128, 129, 157, 189, 216)" at sp_Blitz.sql:4366.</item>
        /// <item>Form B takes the ENDPOINTS of a "through" range, not the range. So 194, 197, 237 and
        /// 255 arrive from that line while 195, 196 and 238 through 254 arrive from form G instead.
        /// The union is right; neither form is right alone.</item>
        /// <item>IT IS LITERAL-SENSITIVE, AND THAT PRODUCES PHANTOM "NEW" IDS ON A VERSION DIFF. Two
        /// were measured on the 8.28 to 8.34 diff. CheckID 69 (High VLF Count) exists in both, but at
        /// 8.28 its trace read 'Running CheckId [%d] (2012 version of Log Info).' and form B missed
        /// it. CheckID 1069 (remote login timeout) exists in both, but at 8.28 its default value was a
        /// multi-line CASE and form E missed it. Neither is a new check. On any future bump, look for
        /// the id in the OLD file before believing it is new.</item>
        /// </list>
        /// </summary>
        internal static SortedSet<int> EmittedCheckIds(string scriptText)
        {
            var ids = new SortedSet<int>();

            void Take(string captured)
            {
                foreach (Match n in AnyInteger.Matches(captured))
                    ids.Add(int.Parse(n.Value, CultureInfo.InvariantCulture));
            }

            foreach (Match m in CheckIdFormA.Matches(scriptText)) Take(m.Groups[1].Value);
            foreach (Match m in CheckIdFormB.Matches(scriptText)) Take(m.Groups[1].Value);
            foreach (Match m in CheckIdFormC.Matches(scriptText)) Take(m.Groups[1].Value);
            foreach (Match m in CheckIdFormD.Matches(scriptText)) Take(m.Groups[1].Value);

            void Region(Regex region, Regex id)
            {
                foreach (Match r in region.Matches(scriptText))
                    foreach (Match m in id.Matches(r.Groups[1].Value))
                        Take(m.Groups[1].Value);
            }

            Region(ConfigurationDefaultsRegion, ConfigurationDefaultsId);
            Region(DatabaseDefaultsRegion, DatabaseDefaultsId);
            Region(DatabaseScopedRegion, DatabaseScopedId);

            return ids;
        }

        /// <summary>
        /// Derived from scripts/sp_Blitz.sql at 8.34 / 20260702 and checked in. 334 ids.
        /// Every one of the 65 ids the live probe observed on .\new2022 is in here, and
        /// FrkLiveSmokeTests asserts that containment on every armed run.
        ///
        /// <para>199 joined the list on 2026-08-26, not because the script changed but because
        /// <see cref="CheckIdFormA"/> could not see its quoted-literal form. It was always emitted.
        /// </para>
        /// </summary>
        internal static readonly int[] ExpectedCheckIds =
        {
            -1, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16, 17, 19, 20, 21, 22, 24, 25, 26, 27, 28,
            29, 30, 31, 32, 33, 34, 35, 36, 37, 40, 41, 42, 44, 45, 46, 47, 48, 49, 50, 51, 53, 55, 56, 57,
            59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83,
            84, 85, 86, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106,
            107, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 121, 122, 123, 124, 125, 126, 128, 130,
            131, 132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145, 146, 147, 148, 149,
            150, 151, 152, 153, 154, 155, 156, 158, 159, 160, 161, 162, 163, 164, 165, 166, 167, 168, 169,
            170, 171, 172, 173, 174, 175, 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 186, 187, 188,
            189, 190, 191, 192, 193, 194, 195, 196, 197, 198, 199, 201, 202, 203, 204, 205, 206, 207, 208, 209,
            210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228,
            229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239, 240, 241, 242, 243, 244, 245, 246, 247,
            248, 249, 250, 251, 252, 253, 254, 255, 256, 257, 258, 259, 260, 261, 262, 263, 264, 265, 266,
            267, 268, 269, 270, 271, 272, 273, 274, 275, 1001, 1002, 1003, 1004, 1005, 1007, 1008, 1009,
            1010, 1011, 1012, 1013, 1014, 1016, 1017, 1018, 1019, 1020, 1021, 1022, 1023, 1024, 1025, 1026,
            1027, 1028, 1029, 1030, 1031, 1032, 1033, 1034, 1035, 1036, 1037, 1038, 1039, 1040, 1041, 1042,
            1043, 1044, 1045, 1046, 1047, 1048, 1049, 1050, 1051, 1052, 1053, 1054, 1055, 1056, 1057, 1058,
            1059, 1060, 1061, 1062, 1063, 1064, 1065, 1066, 1067, 1068, 1069, 1070, 1071, 1072, 1073, 1074,
            1075, 1076, 2000, 2301
        };

        [Fact]
        public void The_vendored_sp_Blitz_emits_exactly_the_checked_in_CheckID_set()
        {
            var actual = EmittedCheckIds(ScriptText("sp_Blitz.sql"));
            var expected = new SortedSet<int>(ExpectedCheckIds);

            var added = actual.Except(expected).ToList();
            var gone = expected.Except(actual).ToList();

            Assert.True(added.Count == 0 && gone.Count == 0,
                "The CheckID set sp_Blitz can emit has moved.\n"
                + "  NEW (in the script, not in the checked-in list): " + Join(added) + "\n"
                + "  GONE (in the list, not in the script): " + Join(gone) + "\n"
                + "Every id here is a scoring-universe decision, not a formality. A NEW id with no "
                + "Config/roadmap-mapping.json entry renders to the client as a nameless "
                + "'sp_Blitz Check N' and still counts as a ding. A GONE id left in the universe is "
                + "a permanent free PASS that inflates the health score. Reconcile both sides, then "
                + "update ExpectedCheckIds.");
        }

        [Fact]
        public void CheckIDs_129_and_157_are_retired_and_only_survive_inside_a_skip_guard()
        {
            // Retired by FRK 20260708 (#3915, outdated-script warnings removed) together with the
            // 20260407 support floor of SQL 2016 SP2 and above: both were "Dangerous Build of SQL
            // Server" checks whose version ranges (2008 through 2014) can no longer be reached.
            var text = ScriptText("sp_Blitz.sql");
            var emitted = EmittedCheckIds(text);

            Assert.DoesNotContain(129, emitted);
            Assert.DoesNotContain(157, emitted);

            // And they are still REFERENCED, in the skip guard. If that goes too, the retirement
            // set in BlitzDashboardService should be revisited rather than left to rot.
            Assert.Contains("CheckID IN (128, 129, 157, 189, 216)", text);
        }

        [Fact]
        public void The_three_new_8_34_CheckIDs_are_present()
        {
            var emitted = EmittedCheckIds(ScriptText("sp_Blitz.sql"));
            Assert.Contains(273, emitted);   // Constitution.md Present
            Assert.Contains(274, emitted);   // Agents.md Present
            Assert.Contains(275, emitted);   // Non-Default Database Config
        }

        [Fact]
        public void The_three_new_8_34_CheckIDs_have_a_roadmap_mapping_entry()
        {
            // Without a map entry a fired id renders to the client as a nameless
            // "sp_Blitz Check 273" and still counts as a ding (BlitzDashboardService, unknown-id
            // branch). The three ids the bump introduced are the ones this lane is answerable for;
            // the pre-existing unmapped ids (22, 201, 204, 227, 228, 2000) are a separate lane and
            // are deliberately NOT asserted here, so this test cannot be red for someone else's debt.
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath("roadmap-mapping.json")));
            var mapped = doc.RootElement.GetProperty("blitzCheckMap").EnumerateArray()
                .Select(e => e.GetProperty("checkId").GetInt32())
                .ToHashSet();

            foreach (var id in new[] { 273, 274, 275 })
                Assert.True(mapped.Contains(id),
                    "CheckID " + id + " is emitted by the vendored sp_Blitz but has no "
                    + "Config/roadmap-mapping.json entry, so a client would see it as an unnamed check.");
        }

        [Fact]
        public void The_retired_CheckIDs_are_out_of_the_roadmap_map_universe()
        {
            // The map still CARRIES 129 and 157 (they are generated from AllCheckTable and this lane
            // does not delete history). The exclusion lives in code, so assert the code, not the file.
            Assert.Contains(129, SQLTriage.Data.Services.BlitzDashboardService.RetiredCheckIds);
            Assert.Contains(157, SQLTriage.Data.Services.BlitzDashboardService.RetiredCheckIds);
        }

        [Fact]
        public void The_quoted_literal_CheckID_form_is_seen_by_the_extractor()
        {
            // audit-r2-02. The instrument named "emits_exactly" could not see 'nnn' AS CheckID, so a
            // live check read as never-emitted. Assert the SHAPE is really in the file, then assert
            // the extractor sees it, so this test cannot pass by the script quietly changing form.
            var text = ScriptText("sp_Blitz.sql");

            Assert.Contains("'199' AS CheckID", text);

            var emitted = EmittedCheckIds(text);
            Assert.True(emitted.Contains(199),
                "CheckID 199 ('There Is An Error With The Default Trace') is written as a QUOTED "
                + "literal in the vendored script. An extractor that requires a bare integer misses "
                + "it, and every consumer that derives 'what can never fire' from this set then "
                + "excludes a LIVE check from scoring. That raises the score and says nothing.");
        }

        [Fact]
        public void The_never_fires_set_is_exactly_the_scoring_universe_minus_what_the_script_emits()
        {
            // audit-r1-02 / audit-r2-01. CheckUniverse's exclusions are a MEASUREMENT, and this is
            // the measurement. Derive it from the two artefacts it claims to describe (the shipped
            // mapping and the vendored script) and demand equality in both directions, so an FRK
            // bump that revives or retires an id fails the build rather than moving a client score.
            var emitted = EmittedCheckIds(ScriptText("sp_Blitz.sql"));

            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath("roadmap-mapping.json")));
            var infoCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Server Info", "Information", "Informational" };

            // The roadmap page's own universe rule: level 1..5, category not informational.
            var scoringUniverse = doc.RootElement.GetProperty("blitzCheckMap").EnumerateArray()
                .Where(e => e.TryGetProperty("level", out var l) && l.GetInt32() >= 1 && l.GetInt32() <= 5)
                .Where(e => !infoCategories.Contains(
                    e.TryGetProperty("category", out var c) ? c.GetString() ?? "" : ""))
                .Select(e => e.GetProperty("checkId").GetInt32())
                .ToHashSet();

            var derived = scoringUniverse
                .Where(id => id > 0 && id < SQLTriage.Data.Services.CheckUniverse.SentinelIdFloor)
                .Where(id => !emitted.Contains(id))
                .OrderBy(id => id)
                .ToArray();

            var production = SQLTriage.Data.Services.CheckUniverse.RetiredCheckIds
                .Concat(SQLTriage.Data.Services.CheckUniverse.UnreachableCheckIds)
                .OrderBy(id => id)
                .ToArray();

            Assert.True(derived.SequenceEqual(production),
                "CheckUniverse's exclusions have drifted from the artefacts they describe.\n"
                + "  DERIVED (mapping's scoring universe minus what sp_Blitz can emit): " + Join(derived.ToList()) + "\n"
                + "  IN CheckUniverse (Retired + Unreachable):                          " + Join(production.ToList()) + "\n"
                + "An id in DERIVED but not in CheckUniverse is a permanent free PASS in the client "
                + "PDF. An id in CheckUniverse but not in DERIVED is a LIVE check that has silently "
                + "stopped being graded. Reconcile against the script, then update CheckUniverse "
                + "with the measurement named in a comment.");
        }

        [Fact]
        public void The_sentinel_floor_keeps_the_sp_triage_custom_ids_out_of_the_sp_Blitz_universe()
        {
            // 9999 sits in the shipped mapping as "Weak SQL Login Passwords". It is the AllCheckTable
            // sentinel for sp_triage custom checks that have no upstream sp_Blitz id, so it can never
            // be a graded sp_Blitz pass. It is excluded by the floor rather than by the derived set
            // above, which is why the derivation is scoped below the floor and this is asserted here.
            Assert.False(SQLTriage.Data.Services.CheckUniverse.CanFire(9999));
            Assert.False(SQLTriage.Data.Services.CheckUniverse.CanFire(
                SQLTriage.Data.Services.CheckUniverse.SentinelIdFloor));

            // And the banner row is not a check either.
            Assert.False(SQLTriage.Data.Services.CheckUniverse.CanFire(-1));

            // The live ids either side of the excluded ones still fire, so the filter is a scalpel.
            Assert.True(SQLTriage.Data.Services.CheckUniverse.CanFire(199));
            Assert.True(SQLTriage.Data.Services.CheckUniverse.CanFire(217));
        }

        // ---------------------------------------------------------------------------------
        // (d) THE SAFETY VALIDATOR STILL LETS BOTH SCRIPTS THROUGH
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData("sp_Blitz.sql")]
        [InlineData("sp_ineachdb.sql")]
        public void The_safety_validator_passes_the_vendored_script(string fileName)
        {
            var text = ScriptText(fileName);

            // The real gate DiagnosticScriptRunner runs at :214 and :217, in that order.
            SqlSafetyValidator.ValidateOrThrow(text, fileName);

            var scope = SqlSafetyValidator.ValidateDatabaseScope(text);
            Assert.True(scope.IsSafe,
                fileName + " was blocked by the database-scope check: " + scope.Reason);
        }

        // ---------------------------------------------------------------------------------
        // THE INSTALL-ORDER CONTRACT
        // ---------------------------------------------------------------------------------

        private sealed record ConfigEntry(string Name, string ScriptPath, int ExecutionOrder,
                                          bool Enabled, string ExecutionParameters);

        private static IReadOnlyList<ConfigEntry> ScriptConfigurations()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath("script-configurations.json")));
            return doc.RootElement.EnumerateArray()
                .Select(e => new ConfigEntry(
                    e.GetProperty("Name").GetString() ?? "",
                    e.GetProperty("ScriptPath").GetString() ?? "",
                    e.GetProperty("ExecutionOrder").GetInt32(),
                    e.GetProperty("Enabled").GetBoolean(),
                    e.TryGetProperty("ExecutionParameters", out var p) ? p.GetString() ?? "" : ""))
                .ToList();
        }

        [Fact]
        public void sp_ineachdb_is_installed_before_sp_Blitz_and_installs_only()
        {
            var configs = ScriptConfigurations();

            var ineachdb = configs.SingleOrDefault(c => c.ScriptPath == "sp_ineachdb.sql");
            var blitz = configs.SingleOrDefault(c => c.ScriptPath == "sp_Blitz.sql");

            Assert.True(ineachdb is not null,
                "Config/script-configurations.json has no entry for sp_ineachdb.sql. 8.34 sp_Blitz "
                + "hard-depends on dbo.sp_ineachdb, so without this entry the run installs sp_Blitz "
                + "and then fails at EXEC on every server it touches.");
            Assert.True(blitz is not null, "Config/script-configurations.json has no sp_Blitz.sql entry.");

            Assert.True(ineachdb!.Enabled,
                "The sp_ineachdb entry is disabled, so the prerequisite would never install.");
            Assert.True(ineachdb.ExecutionOrder < blitz!.ExecutionOrder,
                "sp_ineachdb has ExecutionOrder " + ineachdb.ExecutionOrder + " and sp_Blitz has "
                + blitz.ExecutionOrder + ". The prerequisite must sort FIRST: LoadScriptConfigurations "
                + "and FullAudit.GetSelectedScripts both order by ExecutionOrder, and the production "
                + "loops await each script in turn.");
            Assert.True(string.IsNullOrEmpty(ineachdb.ExecutionParameters),
                "The sp_ineachdb entry is install-only by design. Giving it ExecutionParameters would "
                + "make DiagnosticScriptRunner EXEC a proc whose whole job is to be called BY sp_Blitz.");

            // And the file it points at is really on disk, with the copy rule that puts it beside
            // the binary at run time.
            Assert.True(File.Exists(ScriptPath("sp_ineachdb.sql")));
            var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "SQLTriage.csproj"));
            Assert.Contains("<None Update=\"scripts\\sp_ineachdb.sql\">", csproj);
        }

        [Fact]
        public void The_vendored_sp_Blitz_really_does_depend_on_sp_ineachdb()
        {
            // The premise of the whole item. If a future bump drops the dependency this goes red and
            // the extra config entry can be retired deliberately rather than by accident.
            var text = ScriptText("sp_Blitz.sql");

            var ineachdbCalls = Regex.Matches(text, @"EXEC(UTE)?\s+dbo\.sp_ineachdb", RegexOptions.IgnoreCase).Count;
            Assert.True(ineachdbCalls > 10,
                "sp_Blitz.sql calls dbo.sp_ineachdb only " + ineachdbCalls + " times. At 8.34 it is the "
                + "per-database driver for the whole user-database check block, so a low count means "
                + "the dependency has been unwound and the prerequisite entry should be reconsidered.");

            // The proc it replaced must be GONE as a call. It still appears twice at 8.34, but only
            // inside the header comment that explains the replacement, so match the call form and not
            // the bare name.
            var msforeachCalls = Regex.Matches(text, @"EXEC(UTE)?\s+(master\.(sys|dbo)\.)?sp_MSforeachdb",
                                               RegexOptions.IgnoreCase).Count;
            Assert.True(msforeachCalls == 0,
                "sp_Blitz.sql still CALLS sp_MSforeachdb " + msforeachCalls + " times. 8.34 replaced it "
                + "with sp_ineachdb for Azure SQL DB support; a reappearance means the vendored file is "
                + "not the version this contract was written against.");
        }

        // ---------------------------------------------------------------------------------
        // COMMUNITY-TOOL PINS BEYOND FRK
        // ---------------------------------------------------------------------------------

        [Fact]
        public void The_dbatools_pin_is_the_same_in_both_places_that_download_it()
        {
            // Two sites download dbatools: PowerShellService.DownloadDbatoolsAsync (the in-app
            // button) and Update-Dbatools.ps1 (manual / publish pipeline). A pin that lives in one
            // of them is not a pin. This test is the reason moving it is a single edit-and-fail loop
            // rather than a thing to remember.
            var version = SQLTriage.Data.Services.PowerShellService.PinnedDbatoolsVersion;
            Assert.Matches(@"^\d+\.\d+\.\d+$", version);

            // SITE 1 - the in-app download button.
            //
            // THIS HALF WAS MISSING UNTIL 2026-08-23, and the comment above it already said why it
            // must not be. The test asserted the pin in the .ps1 and nothing else, so deleting
            // "-RequiredVersion {PinnedDbatoolsVersion}" from PowerShellService's own command left
            // all 16 tests in this file GREEN on a real un-pin (mutation log
            // C:\temp\frk-refresh\verify\v-h1.log). Prose is not measurement. Two assertions now,
            // because they fail on different mistakes:
            //   (a) BEHAVIOURAL - the command the app actually builds carries the pin;
            //   (b) TEXTUAL - no Save-Module of dbatools anywhere in the C# source lacks a
            //       -RequiredVersion, so ADDING a second, unpinned call site is caught too. (a)
            //       alone would not see that.
            var command = SQLTriage.Data.Services.PowerShellService
                .BuildDbatoolsSaveModuleCommand(@"C:\some\path\dbatools");
            Assert.Contains("Save-Module -Name dbatools", command);
            Assert.Contains("-RequiredVersion " + version, command);

            var serviceSource = File.ReadAllText(
                Path.Combine(RepoRoot(), "Data", "Services", "PowerShellService.cs"));
            AssertNoUnpinnedSaveModule(serviceSource, "Data/Services/PowerShellService.cs");

            // SITE 2 - the manual / publish-pipeline script.
            var script = File.ReadAllText(Path.Combine(RepoRoot(), "Update-Dbatools.ps1"));
            Assert.Contains("-RequiredVersion " + version, script);
            AssertNoUnpinnedSaveModule(script, "Update-Dbatools.ps1");
        }

        /// <summary>
        /// Every <c>Save-Module ... dbatools</c> occurrence in <paramref name="text"/> must carry a
        /// -RequiredVersion on the same line. Comment lines are skipped first, so the prose ABOUT
        /// the pin ("a floating Save-Module would have taken 2.8.0...") cannot be mistaken for a
        /// call site; that prose exists in both files and would otherwise make this permanently red.
        /// </summary>
        private static void AssertNoUnpinnedSaveModule(string text, string label)
        {
            var offenders = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)
                    || code.StartsWith("#", StringComparison.Ordinal)
                    || code.StartsWith("*", StringComparison.Ordinal))
                    continue;
                if (!Regex.IsMatch(line, @"Save-Module\b[^\r\n]*\bdbatools\b", RegexOptions.IgnoreCase))
                    continue;
                if (line.IndexOf("-RequiredVersion", StringComparison.OrdinalIgnoreCase) < 0)
                    offenders.Add(line.Trim());
            }

            Assert.True(offenders.Count == 0,
                label + " downloads dbatools without -RequiredVersion on " + offenders.Count
                + " line(s), so the app's PowerShell dependency can move with no commit and no "
                + "review: " + string.Join(" || ", offenders));
        }

        // ---------------------------------------------------------------------------------
        // THE MAP'S NAMES vs THE SCRIPT'S NAMES
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// CheckIDs whose roadmap-mapping findingName does NOT match the Finding string sp_Blitz
        /// emits, and the reason each is tolerated. Every one of these is a WORDING difference over
        /// the same subject: roadmap-mapping.json is generated from the corpus AllCheckTable, whose
        /// names are written for a client-facing report rather than copied from the vendor.
        ///
        /// <para>This list exists because the class it tolerates hid a real defect. CheckID 172 was
        /// mapped as "Accelerated Database Recovery (ADR) Enabled" while sp_Blitz emits 172 as
        /// "Operating System Version" - a different check entirely - and the client report rendered
        /// the wrong name for it. Nothing measured that, so nothing caught it: it surfaced only
        /// because a live run's output table was read next to the report it produced. The exemption
        /// list is therefore CLOSED: the test asserts the mismatch set is EXACTLY this dictionary,
        /// so a new mismatch is red on arrival and has to be either fixed or admitted here with a
        /// reason.</para>
        /// </summary>
        private static readonly Dictionary<int, string> ToleratedNameDifferences = new()
        {
            [1]   = "map 'Databases Not Being Backed Up' vs script 'Backups Not Performed Recently' - same subject, report wording.",
            [2]   = "map spells out 'Without Log Backups'; script abbreviates 'w/o Log Backups'.",
            [7]   = "map 'Stored Procedures running at startup' vs script 'Stored Procedure Runs at Startup' - same subject.",
            [14]  = "map 'Corruption checks not optimal' vs script 'Page Verification Not Optimal' - same subject; page verification IS the corruption check.",
            [15]  = "map spells out 'Statistics'; script abbreviates 'Stats'.",
            [26]  = "singular/plural only: 'User Database on C Drive' vs 'User Databases on C Drive'.",
            [51]  = "⚠ THE WEAKEST ENTRY ON THIS LIST. map 'Max Memory Set Too Low' vs script 'Memory Dangerously Low'. Same DOMAIN, not the same cause: sp_Blitz 51 fires on sys.dm_os_sys_memory when the OS has under 256 MB available, which is not the same statement as max server memory being configured too low (that neighbour is 50, 'Max Memory Set Too High', and it matches). Left alone rather than renamed because the name comes from the corpus AllCheckTable and a rename is an authoring decision, not a repair. RULING OWED - see the lane report.",
            [68]  = "map 'DBCC CHECKDB Not Performed Recently' vs script 'Last good DBCC CHECKDB over 2 weeks old' - same subject, threshold stated in the script's wording.",
            [89]  = "map borrows the script's Details sentence ('Availability Groups has automatically repaired corruption'); the script's Finding is the generic 'Database Corruption Detected'. The map name is the MORE specific of the two and is correct for this id.",
            [155] = "map 'Outdated sp_Blitz' vs script 'sp_Blitz is Over 6 Months Old' - same subject. This is the check the 8.34 refresh exists to stop firing.",
            [175] = "map states the threshold ('>16 Data Files'); script says 'a Lot of Data Files'.",
            [203] = "map reuses the FindingsGroup 'DBCC Events' as the name; script's Finding is 'Overall Events'. Imprecise, not wrong - a DBCC-events row really is a DBCC event.",
            [226] = "the FRK self-version check emits three different Findings ('Version Check Failed (...)'); the map names the group 'First Responder Kit'. One name cannot match three, and naming the group is the honest choice.",
            [259] = "map distinguishes the Agent service account; the script emits one shared Finding 'Dangerous Service Account' for both and separates them in Details.",
            [261] = "as 259, ADMIN variant (the map's name also carries a double space, upstream of this repo).",
            [266] = "map reuses the FindingsGroup 'Server Info' as the name; script's Finding is 'Hardware - Memory Counters'. Imprecise, not wrong - it is a Server Info banner row.",
            [275] = "DELIBERATE, and the reason is in the entry's own _provenance: 8.34 emits 275 for TWO unrelated findings ('Accelerated Database Recovery Enabled' from the #DatabaseDefaults table form, and 'Automatic Tuning' here), so the map names the FindingsGroup they share rather than picking one and being wrong about the other half of the rows.",
        };

        [Fact]
        public void The_roadmap_map_names_the_same_check_sp_Blitz_does()
        {
            var pairs = FindingsByCheckId(ScriptText("sp_Blitz.sql"));
            Assert.True(pairs.Count > 120,
                "Only " + pairs.Count + " CheckID/Finding pairs were extracted from sp_Blitz.sql. The "
                + "extraction has stopped working, and a test that extracts nothing agrees with "
                + "everything. Fix the extraction before trusting this file again.");

            var map = MapFindingNames();
            var mismatched = new SortedDictionary<int, string>();

            foreach (var kvp in pairs)
            {
                if (!map.TryGetValue(kvp.Key, out var mapped)) continue;   // map does not cover this id
                if (kvp.Value.Any(f => SameSubject(f, mapped))) continue;
                mismatched[kvp.Key] = "map '" + mapped + "' vs script '"
                                      + string.Join("' / '", kvp.Value.OrderBy(x => x, StringComparer.Ordinal)) + "'";
            }

            var unexpected = mismatched.Where(m => !ToleratedNameDifferences.ContainsKey(m.Key)).ToList();
            Assert.True(unexpected.Count == 0,
                "roadmap-mapping.json names these CheckIDs differently from sp_Blitz, and they are not "
                + "on the tolerated list: "
                + string.Join("; ", unexpected.Select(m => m.Key + ": " + m.Value))
                + ". This is the shape that made CheckID 172 render as the wrong check for the client. "
                + "Either correct findingName/business_translation in Config/roadmap-mapping.json, or - "
                + "if it is only a wording difference over the same subject - add it to "
                + "ToleratedNameDifferences with the reason spelled out.");

            var stale = ToleratedNameDifferences.Keys.Where(id => !mismatched.ContainsKey(id)).ToList();
            Assert.True(stale.Count == 0,
                "These CheckIDs are on the tolerated-wording list but no longer mismatch: "
                + Join(stale) + ". They have been fixed (or the id has left the map or the script). "
                + "Delete them from ToleratedNameDifferences so the list cannot quietly absorb a "
                + "future breakage the way an over-broad exemption always does.");
        }

        /// <summary>
        /// Two names describe the same check if, ignoring case and punctuation, either contains the
        /// other. Deliberately loose: the map is written for a client report and the script for a
        /// DBA, so an exact match would be red on dozens of harmless rows and the test would be
        /// weakened rather than believed. It is still tight enough to have caught 172, where the two
        /// names share no substring at all.
        /// </summary>
        private static bool SameSubject(string scriptFinding, string mappedName)
        {
            var a = Squash(scriptFinding);
            var b = Squash(mappedName);
            if (a.Length == 0 || b.Length == 0) return false;
            return a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
        }

        private static string Squash(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        private static Dictionary<int, string> MapFindingNames()
        {
            var names = new Dictionary<int, string>();
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath("roadmap-mapping.json")));
            if (!doc.RootElement.TryGetProperty("blitzCheckMap", out var arr)) return names;
            foreach (var e in arr.EnumerateArray())
            {
                if (!e.TryGetProperty("checkId", out var ci) || !ci.TryGetInt32(out var id)) continue;
                if (!e.TryGetProperty("findingName", out var fn)) continue;
                var name = fn.GetString();
                if (!string.IsNullOrWhiteSpace(name)) names[id] = name!;
            }
            return names;
        }

        // "172 AS [CheckID]" / "172 AS CheckID".
        private const string CheckIdSelectPattern = @"\b(\d{1,4})\s+AS\s+\[?CheckID\]?";

        // "'Operating System Version' AS [Finding]" / "... AS Finding". Two details are load-bearing,
        // and BOTH were got wrong first and caught by the numbers being absurd:
        //   * the \b after the bare form. Without it this also matches "AS [FindingsGroup]", and the
        //     comparison silently becomes group-name vs check-name (115 "mismatches" instead of 17).
        //   * '{1,2}. Roughly a third of sp_Blitz's checks are built as DYNAMIC SQL, where every
        //     literal is quote-DOUBLED: ''Server Audits Running'' AS Finding. Matching a single quote
        //     captures the EMPTY string between the doubled pair, and an empty capture is a substring
        //     of every name, so those checks silently compared equal to whatever the map said. That
        //     is the same failure this test exists to catch, one level up, so the capture must also
        //     contain a letter to count.
        private const string FindingSelectPattern =
            @"'{1,2}([^']*)'{1,2}\s+AS\s+(?:\[Finding\]|Finding\b)";

        private static readonly Regex CheckIdSelectRx =
            new(CheckIdSelectPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FindingSelectRx =
            new(FindingSelectPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// CheckID -&gt; the Finding literal(s) sp_Blitz emits with it, for the SELECT form only
        /// ("172 AS [CheckID], ... 'Operating System Version' AS [Finding]"). It does not see the
        /// #ConfigurationDefaults / #DatabaseDefaults table forms or the RAISERROR trace, and it does
        /// not need to: those carry no Finding literal beside the id. The pair is taken from the
        /// FIRST Finding after the id and before the next id, so an unrelated later block cannot be
        /// captured.
        /// </summary>
        private static Dictionary<int, List<string>> FindingsByCheckId(string script)
        {
            var found = new Dictionary<int, List<string>>();
            var lines = script.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var m = CheckIdSelectRx.Match(lines[i]);
                if (!m.Success) continue;
                var id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                for (int j = i + 1; j < Math.Min(i + 40, lines.Length); j++)
                {
                    var f = FindingSelectRx.Match(lines[j]);
                    if (f.Success)
                    {
                        var finding = f.Groups[1].Value;
                        // An empty or punctuation-only capture is a substring of every name and would
                        // make this check vacuously true; see FindingSelectPattern.
                        if (finding.Any(char.IsLetter))
                        {
                            if (!found.TryGetValue(id, out var list)) found[id] = list = new List<string>();
                            if (!list.Contains(finding, StringComparer.Ordinal))
                                list.Add(finding);
                        }
                        break;
                    }
                    if (CheckIdSelectRx.IsMatch(lines[j])) break;
                }
            }
            return found;
        }

        private static string Join(IEnumerable<int> ids)
        {
            var list = ids.ToList();
            return list.Count == 0 ? "(none)" : string.Join(", ", list);
        }
    }
}
