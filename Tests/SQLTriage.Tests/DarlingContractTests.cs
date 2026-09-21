/* In the name of God, the Merciful, the Compassionate */

// THE sp_PerfCheck <-> APP CONTRACT, PINNED.
//
// Ruling 8, DECISIONS 2026-08-23 05:00: bundle Erik Darling's sp_PerfCheck UNMODIFIED and surface
// it the way sp_Blitz is surfaced. "Unmodified" and "MIT" are not states a repo holds by itself.
// A vendored third-party script drifts the moment somebody fixes a typo in it, and the licence
// notice is the first casualty of a tidy-up, so both are pinned here rather than promised in prose.
//
// The second half of the file is the wiring. sp_PerfCheck has no @OutputTableName, so unlike
// sp_Blitz there is no persisted table to read: the EXEC goes in SqlQueryForOutput and the rows
// come straight off the wire. That makes TWO settings load-bearing that no other shipped entry
// uses - OutputResultSetIndex (the findings are result set 1, not 0) and EmptyResultIsNormal (no
// findings means a healthy server) - and a config entry that lost either would still run, still
// export a CSV, and still say Success. That is the house defect class, so both are asserted here
// against the real shipped file.
//
// WHAT THIS FILE DOES NOT DO. It reads the script as TEXT and the config as JSON. It runs neither.
// The live proof - install into master, EXEC, capture the nine-column findings, write the CSV - is
// PerfCheckLiveSmokeTests, which is SKIPPED unless PERFCHECK_LIVE_TARGET names an instance.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    public class DarlingContractTests
    {
        // -- The pin. -------------------------------------------------------------------------

        public const string ExpectedPerfCheckVersion = "2.8";
        public const string ExpectedPerfCheckVersionDate = "20260801";

        /// <summary>The upstream release TAG the vendored file came from (not main).</summary>
        public const string UpstreamTag = "Updates_20260801";

        /// <summary>The upstream commit that tag pointed at when the file was vendored.</summary>
        public const string UpstreamCommit = "6165b66a251504d5b88edd10416166d9d1d114d3";

        /// <summary>Lines in the vendored file, counted the way File.ReadAllLines counts them.</summary>
        public const int ExpectedPerfCheckLineCount = 5360;

        /// <summary>
        /// SHA-256 of the upstream file's own bytes: UTF-8, no BOM, LF.
        ///
        /// <para>The lane's design said "no byte-hash pin", and the reason was sound: the repo has
        /// core.autocrlf=true and no *.sql attribute, so the file on disk is CRLF while the git blob
        /// is LF, and a hash of the disk bytes would be a hash of one machine's checkout. That is
        /// the exact trap BPScripts/01. MaintenanceSolution.sql fell into for three weeks.</para>
        ///
        /// <para>Hashing the LINE-ENDING-NORMALISED bytes removes the trap and keeps the strength.
        /// This value is what curl returns for the raw file at the tag above, and it is what
        /// "git show :scripts/sp_PerfCheck.sql | sha256sum" returns, on any platform, whatever the
        /// checkout did to the line endings. A line count cannot tell a changed threshold from an
        /// unchanged one; this can.</para>
        /// </summary>
        public const string ExpectedPerfCheckSha256Lf =
            "5a556dc0cb640e82ebf02b267aa0eac031f3a0782fc2ad42246f8ecb2a332d8a";

        public const string ScriptFileName = "sp_PerfCheck.sql";
        public const string ConfigEntryName = "sp_PerfCheck";
        public const string ExpectedOutputQuery = "EXEC dbo.sp_PerfCheck;";

        /// <summary>
        /// The nine columns of the findings result set, in order. PerfCheckLiveSmokeTests asserts a
        /// real instance returns exactly these, which is what proves OutputResultSetIndex selects
        /// the right set rather than the two-column server banner that precedes it.
        /// </summary>
        public static readonly string[] ExpectedFindingsColumns =
        {
            "check_id", "priority", "priority_label", "category", "finding",
            "database_name", "object_name", "details", "url"
        };

        /// <summary>The two columns of the banner that precedes the findings.</summary>
        public static readonly string[] ServerBannerColumns = { "Server Information", "Details" };

        // -- Locating the REPO sources, never a build-output copy ------------------------------
        // FrkContractTests owns these; reused rather than re-derived so both contract files
        // measure the same tree.

        internal static string ScriptPath() => FrkContractTests.ScriptPath(ScriptFileName);
        internal static string ScriptText() => File.ReadAllText(ScriptPath());

        // -------------------------------------------------------------------------------------
        // (a) THE VERSION STAMP
        // -------------------------------------------------------------------------------------

        [Fact]
        public void The_vendored_sp_PerfCheck_carries_the_pinned_version_stamp()
        {
            // Darling Data stamps LOWERCASE and N-prefixed, which is why FrkContractTests' regex
            // (@Version = '...') does not and must not match this file. Two vendors, two shapes.
            var m = Regex.Match(
                ScriptText(),
                @"@version\s*=\s*N'(?<v>[^']*)'\s*,\s*@version_date\s*=\s*N'(?<d>[^']*)'");

            Assert.True(m.Success,
                ScriptFileName + " has no @version / @version_date assignment at all. Either the "
                + "vendored file is not sp_PerfCheck, or upstream changed how it stamps itself.");

            Assert.Equal(ExpectedPerfCheckVersion, m.Groups["v"].Value);
            Assert.Equal(ExpectedPerfCheckVersionDate, m.Groups["d"].Value);
        }

        // -------------------------------------------------------------------------------------
        // (b) THE MIT NOTICE SURVIVES IN THE COPY
        // -------------------------------------------------------------------------------------

        [Fact]
        public void The_vendored_sp_PerfCheck_keeps_the_upstream_MIT_notice()
        {
            // The licence is the condition on which this file may be shipped at all, and it lives
            // INSIDE the script: a header block at the top and the full text inside the @help
            // output. Both must survive.
            var text = ScriptText();

            Assert.Contains("MIT License", text);
            Assert.Contains("Darling Data, LLC", text);
            Assert.Contains("Permission is hereby granted", text);
            Assert.Contains("erikdarling.com", text,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_repo_attributes_sp_PerfCheck_to_its_author()
        {
            // The in-file notice satisfies MIT. AUTHORS.md is what a reader of the product sees,
            // and it is prose, so it is gated by the same evidence as the thing it describes.
            var authors = File.ReadAllText(Path.Combine(FrkContractTests.RepoRoot(), "AUTHORS.md"));

            Assert.Contains("sp_PerfCheck", authors);
            Assert.Contains("Darling Data, LLC", authors);
            Assert.Contains("DarlingData Performance Monitor", authors);
        }

        // -------------------------------------------------------------------------------------
        // (c) UNMODIFIED
        // -------------------------------------------------------------------------------------

        [Fact]
        public void The_vendored_sp_PerfCheck_is_the_upstream_file_unmodified()
        {
            var path = ScriptPath();

            Assert.Equal(ExpectedPerfCheckLineCount, File.ReadAllLines(path).Length);

            // Normalise CRLF -> LF before hashing. See ExpectedPerfCheckSha256Lf: the checkout's
            // line endings are a property of core.autocrlf, not of the file's content, and pinning
            // them here would make this test pass or fail by machine.
            var normalised = File.ReadAllBytes(path).Where(b => b != (byte)'\r').ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(normalised)).ToLowerInvariant();

            Assert.True(ExpectedPerfCheckSha256Lf == hash,
                "scripts/" + ScriptFileName + " is not the upstream file any more. Expected "
                + ExpectedPerfCheckSha256Lf + " (the sha256 of the raw file at tag " + UpstreamTag
                + ", commit " + UpstreamCommit + ", with line endings normalised to LF) and got "
                + hash + ". The bundle ships this script UNMODIFIED under MIT: if upstream released "
                + "a new version, re-vendor it and bump every constant in this file. Do not patch "
                + "the vendored copy.");
        }

        [Fact]
        public void The_vendored_sp_PerfCheck_installs_a_procedure_and_persists_nothing()
        {
            // Why the app is allowed to run this against a client's production server: it creates
            // one procedure in master and every table it builds is a #temp. If a future version
            // starts creating a real table, or a job, or an Extended Events session, that is a
            // different decision from the one Adrian ruled on and it has to be made again.
            var text = ScriptText();

            // The name must be followed by its column list. Matching a bare word after
            // "CREATE TABLE" also matches the PROSE in this file ("Create table for database scoped
            // configurations"), and a comment is not a table.
            var createTables = Regex.Matches(
                text, @"CREATE\s+TABLE\s+(?<name>[#\[\w][\w\[\]\.\$#@]*)\s*\(",
                RegexOptions.IgnoreCase);
            Assert.True(createTables.Count == 13,
                "sp_PerfCheck " + ExpectedPerfCheckVersion + " builds 13 tables and this copy builds "
                + createTables.Count + ". Either the file changed or the match above no longer knows "
                + "how a CREATE TABLE is written here, and this test would then be vacuous.");

            var persistent = createTables
                .Select(m => m.Groups["name"].Value)
                .Where(n => !n.StartsWith("#", StringComparison.Ordinal))
                .ToList();

            Assert.True(persistent.Count == 0,
                "sp_PerfCheck now creates a table that is not a #temp: "
                + string.Join(", ", persistent)
                + ". It was bundled on the basis that it leaves nothing behind on a client server "
                + "but the procedure itself.");

            Assert.DoesNotContain("CREATE EVENT SESSION", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sp_add_job", text, StringComparison.OrdinalIgnoreCase);
        }

        // -------------------------------------------------------------------------------------
        // (d) THE SAFETY VALIDATOR STILL LETS IT THROUGH
        // -------------------------------------------------------------------------------------

        [Fact]
        public void The_safety_validator_passes_the_vendored_sp_PerfCheck()
        {
            var text = ScriptText();

            // The real gate DiagnosticScriptRunner runs over the script file, in this order.
            SqlSafetyValidator.ValidateOrThrow(text, ScriptFileName);

            var scope = SqlSafetyValidator.ValidateDatabaseScope(text);
            Assert.True(scope.IsSafe,
                ScriptFileName + " was blocked by the database-scope check: " + scope.Reason);
        }

        [Fact]
        public void The_safety_validator_passes_the_config_entrys_own_sql()
        {
            // DiagnosticScriptRunner validates ExecutionTest, ExecutionParameters and
            // SqlQueryForOutput separately from the script file, and NOTHING in the repo tested the
            // config strings before this. They matter more here than for any other entry, because
            // for sp_PerfCheck the EXEC itself lives in SqlQueryForOutput: a blocked string is not a
            // missing export, it is the whole run.
            //
            // Note the asymmetry that makes this worth its own test. The validator's first allowed
            // exception is evaluated over the WHOLE batch, and the script file earns that exemption
            // because it is full of SELECT ... FROM sys. A short config string has no such cover.
            var entry = Entry();

            foreach (var pair in new[]
                     {
                         ("ExecutionTest", entry.ExecutionTest),
                         ("ExecutionParameters", entry.ExecutionParameters),
                         ("SqlQueryForOutput", entry.SqlQueryForOutput),
                     })
            {
                if (string.IsNullOrEmpty(pair.Item2)) continue;
                SqlSafetyValidator.ValidateOrThrow(pair.Item2, ConfigEntryName + " (" + pair.Item1 + ")");
            }
        }

        // -------------------------------------------------------------------------------------
        // (e) THE CONFIG ENTRY
        // -------------------------------------------------------------------------------------

        internal sealed record Cfg(
            string Name, string ScriptPath, string ExecutionTest, string ExecutionParameters,
            string SqlQueryForOutput, bool Enabled, int TimeoutSeconds, int ExecutionOrder,
            bool ExportToCsv, int OutputResultSetIndex, bool EmptyResultIsNormal, string Description);

        internal static IReadOnlyList<Cfg> Configurations()
        {
            using var doc = JsonDocument.Parse(
                File.ReadAllText(FrkContractTests.ConfigPath("script-configurations.json")));

            return doc.RootElement.EnumerateArray()
                .Select(e => new Cfg(
                    e.GetProperty("Name").GetString() ?? "",
                    e.GetProperty("ScriptPath").GetString() ?? "",
                    e.TryGetProperty("ExecutionTest", out var t) ? t.GetString() ?? "" : "",
                    e.TryGetProperty("ExecutionParameters", out var p) ? p.GetString() ?? "" : "",
                    e.TryGetProperty("SqlQueryForOutput", out var q) ? q.GetString() ?? "" : "",
                    e.GetProperty("Enabled").GetBoolean(),
                    e.GetProperty("TimeoutSeconds").GetInt32(),
                    e.GetProperty("ExecutionOrder").GetInt32(),
                    e.GetProperty("ExportToCsv").GetBoolean(),
                    e.TryGetProperty("OutputResultSetIndex", out var i) ? i.GetInt32() : 0,
                    e.TryGetProperty("EmptyResultIsNormal", out var n) && n.GetBoolean(),
                    e.TryGetProperty("Description", out var d) ? d.GetString() ?? "" : ""))
                .ToList();
        }

        internal static Cfg Entry()
        {
            var entry = Configurations().SingleOrDefault(c => c.ScriptPath == ScriptFileName);
            Assert.True(entry is not null,
                "Config/script-configurations.json has no entry for " + ScriptFileName
                + ", so the script is vendored but unreachable: nothing lists it on Full Audit and "
                + "nothing installs it.");
            return entry!;
        }

        [Fact]
        public void The_config_entry_reads_the_findings_and_not_the_server_banner()
        {
            var entry = Entry();

            Assert.Equal(ConfigEntryName, entry.Name);
            Assert.True(entry.Enabled, "a disabled entry is not offered on Full Audit at all");
            Assert.True(entry.ExportToCsv);
            Assert.Equal(ExpectedOutputQuery, entry.SqlQueryForOutput);
            Assert.Equal(900, entry.TimeoutSeconds);

            Assert.True(string.IsNullOrEmpty(entry.ExecutionParameters),
                "sp_PerfCheck has no @OutputTableName, so there is nothing for ExecutionParameters "
                + "to do: the EXEC is the output query. Filling it in would run the whole procedure "
                + "twice per audit and throw the first run's rows away.");
            Assert.True(string.IsNullOrEmpty(entry.ExecutionTest),
                "ExecutionTest exists to ask whether persisted output is still fresh. sp_PerfCheck "
                + "persists nothing, so any test here would be answering a question about a table "
                + "that does not exist.");

            Assert.Equal(1, entry.OutputResultSetIndex);
            Assert.True(entry.EmptyResultIsNormal,
                "sp_PerfCheck emits one row per problem found, so zero rows is a healthy instance. "
                + "With this false, Full Audit raises a No-Rows issue on every clean server and the "
                + "operator learns to dismiss the post-run modal unread.");
        }

        [Fact]
        public void The_config_entry_says_what_a_non_sysadmin_run_actually_does()
        {
            // WHAT THIS SCRIPT REALLY DOES WITH PERMISSIONS, and the lane's design brief had it
            // wrong. The brief read the two DECLARE initialisers, saw both @is_sysadmin and
            // @has_view_server_state set from IS_SRVROLEMEMBER('sysadmin'), and concluded that a
            // non-sysadmin gets a hollow run full of "Information unavailable". It does not: when
            // @is_sysadmin is 0 the script PROBES the permission for real, by reading a DMV inside
            // sp_executesql and setting the flag on success. A VIEW SERVER STATE login therefore
            // keeps the server-state checks; what it loses is the sysadmin-only work.
            //
            // Proved live on 2026-08-24 against SQL 2022: 15 findings as sysadmin, and the script
            // named the shortfall itself as check_id 5000 "Inadequate permissions". The
            // NON-SYSADMIN half is a weaker measurement and is written as one: that instance
            // accepts Windows authentication only, so no real VIEW SERVER STATE login could
            // connect and the probe ran under EXECUTE AS LOGIN impersonation, which additionally
            // blocks cross-database reads unless the database is TRUSTWORTHY. Under it the run
            // succeeded and returned 4 findings.
            //
            // The Description is what the operator reads, so it is gated on the same evidence -
            // including the instrument. A number that arrived by impersonation and is printed as
            // if it arrived by connection is the house's "prose ahead of its evidence" class, in
            // the one place a client-facing DBA reads it.
            var text = ScriptText();
            Assert.Contains("IS_SRVROLEMEMBER", text, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                Regex.IsMatch(text,
                    @"IF\s+@is_sysadmin\s*=\s*0.{0,600}@has_view_server_state\s*=\s*1",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline),
                "sp_PerfCheck no longer probes VIEW SERVER STATE when the caller is not sysadmin. "
                + "The config Description tells the operator that it does, so either the script "
                + "changed or the Description is now a lie.");

            var description = Entry().Description;
            Assert.Contains("sysadmin", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("VIEW SERVER STATE", description, StringComparison.OrdinalIgnoreCase);

            // If the Description states a non-sysadmin finding count, it states how that count was
            // obtained. Strip the qualifier and this goes red with the number still sitting there.
            Assert.Contains("EXECUTE AS LOGIN", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("impersonation", description, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_config_entry_runs_after_sp_Blitz_and_the_file_is_wired_into_the_build()
        {
            var configs = Configurations();
            var entry = Entry();
            var blitz = configs.Single(c => c.ScriptPath == "sp_Blitz.sql");

            Assert.True(entry.ExecutionOrder > blitz.ExecutionOrder,
                "sp_PerfCheck is ordered after sp_Blitz so the longer-standing audit is the one that "
                + "runs first when an operator ticks both. It is " + entry.ExecutionOrder
                + " and sp_Blitz is " + blitz.ExecutionOrder + ".");

            Assert.True(configs.Select(c => c.ExecutionOrder).Distinct().Count() == configs.Count,
                "two entries share an ExecutionOrder, so the order Full Audit runs them in is "
                + "whatever the sort happens to do with a tie.");

            Assert.True(File.Exists(ScriptPath()));

            // Without this line the file is in the repo and NOT beside the binary, and the runner
            // resolves scripts/ relative to the working directory: every run would throw
            // "Script not found". ScriptCensusTests enforces the same rule for every script; this
            // says it once more for the one this file is about, so a failure names it.
            var csproj = File.ReadAllText(Path.Combine(FrkContractTests.RepoRoot(), "SQLTriage.csproj"));
            Assert.Contains("<None Update=\"scripts\\sp_PerfCheck.sql\">", csproj);
        }

        // -------------------------------------------------------------------------------------
        // (f) INDEPENDENCE
        // -------------------------------------------------------------------------------------

        [Fact]
        public void sp_PerfCheck_needs_no_prerequisite_procedure()
        {
            // sp_Blitz 8.34 needs dbo.sp_ineachdb installed first, which is why that entry exists
            // and why its ExecutionOrder matters. sp_PerfCheck drives its own cursor over the
            // database list, so it needs nothing installed beside it. If that ever changes, the
            // config entry needs a prerequisite of its own and this is the test that says so.
            var text = ScriptText();

            Assert.DoesNotContain("sp_ineachdb", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sp_MSforeachdb", text, StringComparison.OrdinalIgnoreCase);

            Assert.True(
                Regex.IsMatch(text, @"DECLARE\s+@database_cursor\s+CURSOR", RegexOptions.IgnoreCase),
                "the claim above rests on sp_PerfCheck walking the databases itself; it no longer "
                + "declares its own cursor, so re-check what drives the per-database checks.");
        }

        // -------------------------------------------------------------------------------------
        // (g) ANTI-VACUITY
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Every check_id the vendored script can emit, read out of the source.
        ///
        /// <para>TWO FORMS, and knowing only one of them is how a textual extraction lies. The
        /// SELECT form writes "check_id = 1007" in a column list; the VALUES form writes the id as
        /// the first element of an INSERT INTO #results tuple, with no "check_id" anywhere near it.
        /// The first draft of this method knew only the SELECT form and reported 31 ids, which is
        /// also the number the lane's design brief carried. The live harness then watched a real
        /// instance fire 1003, 5000 and 6002, none of which were in that 31. Reading both forms
        /// gives 57. PerfCheckLiveSmokeTests cross-checks every id a real run emits against this
        /// list for exactly that reason: an extraction that misses a form goes unnoticed otherwise.</para>
        /// </summary>
        public static IReadOnlyList<int> EmittedCheckIds()
        {
            var text = ScriptText();

            var selectForm = Regex.Matches(text, @"check_id\s*=\s*(?<id>\d+)")
                .Select(m => int.Parse(m.Groups["id"].Value));

            // The first integer of the first VALUES tuple that follows a mention of #results.
            var valuesForm = Regex.Matches(text, @"#results\b")
                .Select(m => Regex.Match(
                    text.Substring(m.Index, Math.Min(3000, text.Length - m.Index)),
                    @"VALUES\s*\(\s*(?<id>\d+)\s*,"))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups["id"].Value));

            return selectForm.Concat(valuesForm).Distinct().OrderBy(i => i).ToList();
        }

        [Fact]
        public void The_vendored_sp_PerfCheck_can_actually_emit_findings()
        {
            // The anti-vacuity guard. Every other test in this file would still pass against a file
            // gutted down to its licence header and its version stamp, and the live smoke test
            // would still see two result sets, because the findings set is allowed to be empty.
            // This is what says the script still contains an audit.
            var ids = EmittedCheckIds();

            Assert.True(ids.Count >= 57,
                "sp_PerfCheck " + ExpectedPerfCheckVersion + " emits 57 distinct check_ids and this "
                + "copy emits " + ids.Count + ": " + string.Join(", ", ids)
                + ". Fewer means either the vendored file lost checks or the extraction above no "
                + "longer knows how they are written.");

            // One from each emission form, and both ends of the range, so a regex that matched one
            // shape and missed the other is not enough to pass. 1003, 5000 and 6002 are VALUES-form
            // ids that a real instance was OBSERVED to fire on 2026-08-24.
            Assert.Contains(1007, ids);     // SELECT form
            Assert.Contains(7104, ids);     // SELECT form
            Assert.Contains(1003, ids);     // VALUES form, fired live
            Assert.Contains(5000, ids);     // VALUES form, fired live
            Assert.Contains(6002, ids);     // VALUES form, fired live
        }
    }
}
