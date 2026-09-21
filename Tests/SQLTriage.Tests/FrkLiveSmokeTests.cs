/* In the name of God, the Merciful, the Compassionate */

// THE LIVE PROOF FOR THE 8.34 REFRESH. Everything FrkContractTests pins is TEXT; this file is
// the only thing that shows the refreshed kit actually installs and runs, and that what comes
// back still fits the app.
//
// It drives the REAL path, not a reconstruction of it: the real
// Config/script-configurations.json entries, the real DiagnosticScriptRunner (which installs by
// splitting on GO, runs SqlSafetyValidator, honours ExecutionTest, then EXECs the production
// parameter string), the real ExportToCsv writer, the real AuditOutputScanner, and the real
// BlitzDashboardService.BuildCatalog / ComputeInstanceReport.
//
// WHAT IT PROVES WHEN ARMED
//   1. sp_ineachdb installs FIRST, from the new config entry, and sp_Blitz installs after it.
//   2. sp_Blitz 8.34 EXECs to completion with the exact production parameter string.
//   3. master.dbo.sqldba_sp_Blitz_output still has the twelve columns the shipped select list
//      projects - compared against the CREATE TABLE inside the vendored script, not a list typed
//      here - and the CSV the app writes carries those twelve column NAMES, in that order.
//      That this is ALSO the shape the Export Pack accepts is a different claim and is pinned
//      somewhere else: Portal/SpBlitzAppCsvContractTests reads the canonical header off the
//      Portal type itself. It cannot be asserted in this file, which compiles into the
//      community assembly, where that whole tree is Compile-Removed. Until 2026-08-24 this
//      line claimed the table's columns proved the Export Pack dependency; they did not, and
//      the pack had been dropping sp_Blitz for its whole life - every pack since v1 (2026-07-23) - while this test was green.
//   4. CheckID 155 "sp_Blitz is Over 6 Months Old" is GONE. It was firing on every client audit
//      because the deployed copy was nine months old; that is the client-visible symptom this
//      whole lane exists to remove.
//   5. The CSV the app itself writes still parses, and a dashboard report builds from it.
//
// INERT unless armed. LiveFactAttribute reports SKIPPED (not passed) when FRK_LIVE_TARGET is
// unset, and RequireTarget asserts the same variable inside the body so the test FAILS rather
// than passing vacuously if that attribute is ever weakened.
//
// INVOCATION (first armed run 2026-08-23 against .\new2022, SQL 2022 16.0.4262.2):
//   $env:FRK_LIVE_TARGET = ".\new2022"
//   $env:FRK_LIVE_EVIDENCE_DIR = "C:\temp\frk-refresh\live"
//   dotnet test SQLTriage.sln -c Debug --no-build --filter "FullyQualifiedName~FrkLiveSmokeTests"
//
// FIXTURE CONTRACT, and it is a real one.
//   * FRK_LIVE_TARGET must name a SQL instance the caller is happy to have sp_ineachdb and
//     sp_Blitz INSTALLED INTO MASTER on, because that is exactly what the app does to a client
//     server on every run. Point it at a test instance, never at production and never at a
//     read-only replica.
//   * The test DROPS master.dbo.sqldba_sp_Blitz_output before the run. That table is SQLTriage's
//     own artefact, and dropping it is what makes the run a real one: the shipped ExecutionTest
//     returns ToRun=0 when the last CheckDate is under a day old, so without the drop a second
//     run inside 24 hours would skip the EXEC and this file would prove nothing.
//   * It plants no databases and creates nothing else.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public class FrkLiveSmokeTests
{
    private readonly ITestOutputHelper _out;
    public FrkLiveSmokeTests(ITestOutputHelper output) => _out = output;
    private void Line(string s) => _out.WriteLine(s);

    private static string? Target => Environment.GetEnvironmentVariable("FRK_LIVE_TARGET");
    private static string? EvidenceDir => Environment.GetEnvironmentVariable("FRK_LIVE_EVIDENCE_DIR");

    /// <summary>
    /// The guard behind <see cref="LiveFactAttribute"/>. If that attribute is ever weakened or
    /// removed, the body must FAIL rather than pass vacuously, the same shape
    /// IndexAnalysisLiveSmokeTests.RequireTarget uses.
    /// </summary>
    private static string RequireTarget()
    {
        Assert.False(string.IsNullOrWhiteSpace(Target),
            "FRK_LIVE_TARGET is not set, so this test has no instance to install into and nothing "
            + "to assert. It should have been SKIPPED by LiveFactAttribute; if it ran, that "
            + "attribute is no longer doing its job.");
        return Target!;
    }

    /// <summary>
    /// The physical columns of the output table sp_Blitz writes with @OutputTableName, read out of
    /// the CREATE TABLE in the VENDORED script rather than retyped here. A kit refresh that changes
    /// the table therefore moves this expectation and the shipped select list together, and
    /// BlitzCsvContractTests fails if only one of them moves.
    /// </summary>
    private static string[] ExpectedOutputColumns =>
        BlitzCsvContractTests.OutputTableColumnsFromScript().ToArray();

    private static string ConnString(string target) =>
        new SqlConnectionStringBuilder
        {
            DataSource = target,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            ApplicationName = "SQLTriage.Tests.FrkLiveSmoke",
        }.ConnectionString;

    private static List<(string Name, DateTime Modified)> ProcedureSnapshot(string target)
    {
        var rows = new List<(string, DateTime)>();
        using var conn = new SqlConnection(ConnString(target));
        conn.Open();
        using var cmd = new SqlCommand(
            "SELECT name, modify_date FROM master.sys.procedures ORDER BY name", conn) { CommandTimeout = 60 };
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetDateTime(1)));
        return rows;
    }

    private static void Exec(string target, string sql)
    {
        using var conn = new SqlConnection(ConnString(target));
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.ExecuteNonQuery();
    }

    private static List<string> OutputTableColumns(string target)
    {
        var cols = new List<string>();
        using var conn = new SqlConnection(ConnString(target));
        conn.Open();
        using var cmd = new SqlCommand(
            "SELECT c.name FROM master.sys.columns c "
            + "WHERE c.object_id = OBJECT_ID('master.dbo.sqldba_sp_Blitz_output') ORDER BY c.column_id",
            conn) { CommandTimeout = 60 };
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) cols.Add(reader.GetString(0));
        return cols;
    }

    private static void WriteEvidence(string fileName, string content)
    {
        var dir = EvidenceDir;
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content, new UTF8Encoding(false));
    }

    [LiveFact("FRK_LIVE_TARGET")]
    public async Task The_refreshed_kit_installs_runs_and_still_feeds_the_blitz_dashboard()
    {
        var target = RequireTarget();

        // ---- The runner resolves scripts/ BESIDE THE BINARY ---------------------------------
        // CORRECTED 2026-08-24. This block used to assert the CWD-relative behaviour, because
        // DiagnosticScriptRunner did Path.Combine("scripts", config.ScriptPath). That was a defect,
        // not a contract: the autostart Run key and the Windows service both start the process with
        // a working directory of C:\Windows\System32, so those launch vectors could never resolve a
        // script. The runner now does Path.Combine(AppContext.BaseDirectory, "scripts", ...), which
        // is what ExportPackRunner.cs:703 and AutoUpdateService.cs:791 already did. Assert the
        // folder is beside the binary, and say plainly what to do if it moves, rather than silently
        // repointing anything. InstallerScriptResolutionLiveTests is the harness that proves the
        // resolution is now working-directory independent.
        var scriptsHere = Path.Combine(AppContext.BaseDirectory, "scripts", "sp_Blitz.sql");
        Assert.True(File.Exists(scriptsHere),
            "DiagnosticScriptRunner reads scripts/ from AppContext.BaseDirectory, and there is no "
            + "scripts/sp_Blitz.sql under " + AppContext.BaseDirectory + ". Rebuild so the csproj "
            + "copy rules put the script beside the test binary.");

        Line("target        : " + target);
        Line("working dir   : " + Directory.GetCurrentDirectory());

        // ---- BEFORE snapshot ---------------------------------------------------------------
        var before = ProcedureSnapshot(target);
        WriteEvidence("procedures-before.txt",
            string.Join(Environment.NewLine,
                before.Select(p => p.Name + "\t" + p.Modified.ToString("O", CultureInfo.InvariantCulture))));
        Line("procs before  : " + before.Count
             + "  (sp_Blitz present: " + before.Any(p => p.Name == "sp_Blitz")
             + ", sp_ineachdb present: " + before.Any(p => p.Name == "sp_ineachdb") + ")");

        // ---- Fixture reset: force the shipped ExecutionTest to return ToRun=1 ---------------
        Exec(target, "IF OBJECT_ID('master.dbo.sqldba_sp_Blitz_output') IS NOT NULL "
                     + "DROP TABLE master.dbo.sqldba_sp_Blitz_output;");

        // ---- The REAL path -----------------------------------------------------------------
        var runner = new DiagnosticScriptRunner(
            new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
            NullLogger<DiagnosticScriptRunner>.Instance);

        var connection = new ServerConnection
        {
            ServerNames = target,
            Database = "master",
            UseWindowsAuthentication = true,
            TrustServerCertificate = true,
        };

        var configs = runner.LoadScriptConfigurations();
        Assert.NotEmpty(configs);

        var ineachdb = configs.SingleOrDefault(c => c.ScriptPath == "sp_ineachdb.sql");
        var blitz = configs.SingleOrDefault(c => c.ScriptPath == "sp_Blitz.sql");
        Assert.True(ineachdb is not null, "no sp_ineachdb entry in the loaded configuration");
        Assert.True(blitz is not null, "no sp_Blitz entry in the loaded configuration");
        Assert.True(ineachdb!.ExecutionOrder < blitz!.ExecutionOrder);

        // Run them in the order the production loop would, sequentially, one await each.
        var ineachdbResult = await runner.ExecuteScriptAsync(ineachdb, connection, target);
        Line("sp_ineachdb   : success=" + ineachdbResult.Success + " err=" + (ineachdbResult.ErrorMessage ?? "none"));
        Assert.True(ineachdbResult.Success,
            "installing sp_ineachdb failed: " + (ineachdbResult.ErrorMessage ?? "no message"));

        // The prerequisite must EXIST before sp_Blitz is allowed to run. This is the single
        // assertion that the whole option (a) design rests on.
        var afterPrereq = ProcedureSnapshot(target);
        Assert.True(afterPrereq.Any(p => p.Name == "sp_ineachdb"),
            "dbo.sp_ineachdb is still absent from master after its install entry ran, so sp_Blitz 8.34 "
            + "cannot work on this server.");

        var blitzResult = await runner.ExecuteScriptAsync(blitz, connection, target);
        Line("sp_Blitz      : success=" + blitzResult.Success + " rows=" + blitzResult.RowsAffected
             + " err=" + (blitzResult.ErrorMessage ?? "none"));
        Assert.True(blitzResult.Success, "sp_Blitz run failed: " + (blitzResult.ErrorMessage ?? "no message"));
        Assert.True(string.IsNullOrEmpty(blitzResult.ErrorMessage),
            "sp_Blitz completed but the runner recorded a warning, which on this path means the EXEC "
            + "itself threw and only the output query succeeded: " + blitzResult.ErrorMessage);
        Assert.True(blitzResult.Results is { Count: > 0 }, "sp_Blitz returned no rows at all");

        // ---- 3. The output table's physical shape -------------------------------------------
        var columns = OutputTableColumns(target);
        WriteEvidence("output-table-columns.txt", string.Join(Environment.NewLine, columns));
        Line("output cols   : " + string.Join(", ", columns));
        Assert.Equal(ExpectedOutputColumns, columns.ToArray());

        // ---- 4. CheckID 155 is gone ---------------------------------------------------------
        var firedIds = blitzResult.Results!
            .Where(r => r.TryGetValue("CheckID", out var v) && v is not null)
            .Select(r => Convert.ToInt32(r["CheckID"], CultureInfo.InvariantCulture))
            .ToList();
        var distinctFired = firedIds.Distinct().OrderBy(i => i).ToList();
        WriteEvidence("fired-checkids.txt", string.Join(", ", distinctFired));
        Line("rows          : " + blitzResult.Results.Count + ", distinct CheckIDs: " + distinctFired.Count);
        Line("fired         : " + string.Join(", ", distinctFired));

        Assert.DoesNotContain(155, distinctFired);
        Assert.DoesNotContain(129, distinctFired);
        Assert.DoesNotContain(157, distinctFired);

        // ---- The completeness check on the OFFLINE contract ---------------------------------
        // FrkContractTests.ExpectedCheckIds is derived by reading the script as text, and a textual
        // extraction can miss a whole emission form without anything going red. It did: the first
        // cut of that list knew two of the seven forms, and this assertion is what caught it. So the
        // two instruments are wired together on purpose. Every id an instance really emits must be a
        // member of the checked-in set, or the offline contract is measuring something narrower than
        // the script does.
        var textualSet = FrkContractTests.ExpectedCheckIds.ToHashSet();
        var firedButUnknownToTheContract = distinctFired.Where(id => !textualSet.Contains(id)).ToList();
        Assert.True(firedButUnknownToTheContract.Count == 0,
            "This instance emitted CheckIDs that FrkContractTests.ExpectedCheckIds does not contain: "
            + string.Join(", ", firedButUnknownToTheContract)
            + ". The textual extraction in FrkContractTests.EmittedCheckIds is missing an emission "
            + "form. Widen it and regenerate the list; do not add the ids by hand.");

        // ---- 5. The CSV the app itself writes, through the real scanner ----------------------
        var csv = runner.ExportToCsv(blitzResult);
        Assert.False(string.IsNullOrWhiteSpace(csv));

        // The CSV's own header, not the table's. These are two different measurements and only
        // one of them was ever taken here: the writer used to prepend a ServerName column of its
        // own, so the file had thirteen fields and two of them shared a name while this test
        // reported the table's twelve and passed.
        var csvHeader = BlitzCsvContractTests.Header(csv);
        WriteEvidence("app-written-header.txt", string.Join(", ", csvHeader));
        Assert.Equal(ExpectedOutputColumns, csvHeader.ToArray());

        var csvDir = Path.Combine(Path.GetTempPath(), "frk-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(csvDir);
        try
        {
            var safeName = string.Join("_", target.Split(Path.GetInvalidFileNameChars()));
            var csvPath = Path.Combine(csvDir,
                safeName + "_sp_Blitz_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
            File.WriteAllText(csvPath, csv);
            WriteEvidence("app-written-sp_Blitz.csv", csv);
            Line("csv header    : " + csv.Split('\n')[0].Trim());

            var scanner = new AuditOutputScanner(NullLogger<AuditOutputScanner>.Instance, csvDir);
            var scanned = (await scanner.ScanAsync()).Files;
            Assert.True(scanned.Count == 1,
                "the scanner found " + scanned.Count + " audit files in a directory holding exactly one "
                + "app-written sp_Blitz CSV.");
            var file = scanned[0];
            Assert.Equal(AuditFileType.SpBlitz, file.FileType);
            await scanner.LoadFiredChecksAsync(file);
            Assert.True(file.FiredCheckCounts.Count > 0, "the scanner parsed no fired CheckIDs from the CSV");
            Assert.DoesNotContain(155, file.FiredCheckCounts.Keys);

            // ---- The real catalog and the real report ---------------------------------------
            var mapPath = Path.Combine(FrkContractTests.RepoRoot(), "Config", "roadmap-mapping.json");
            var catalog = BlitzDashboardService.BuildCatalog(
                Array.Empty<SqlCheck>(), File.ReadAllText(mapPath), NullLogger<BlitzDashboardService>.Instance);
            Assert.True(catalog.Count > 100, "roadmap-mapping produced only " + catalog.Count + " catalog entries");
            Assert.False(catalog.ContainsKey(129));
            Assert.False(catalog.ContainsKey(157));

            var report = BlitzDashboardService.ComputeInstanceReport(file, catalog);
            Line("report        : score=" + report.HealthScore + " universe=" + report.UniverseSize
                 + " fired=" + report.ChecksFired + " info=" + report.InfoFired
                 + " unclassified=" + report.UnclassifiedFired);
            Assert.True(report.UniverseSize > 0);
            Assert.DoesNotContain(report.Findings, f => f.BlitzCheckId == 155);

            // 273/274/275: if they fired on this instance they must render with a NAME, not as
            // "sp_Blitz Check N". If they did not fire, the catalog must still resolve them by id,
            // which is the same guarantee one step earlier in the chain.
            var newIdReport = new StringBuilder();
            foreach (var id in new[] { 273, 274, 275 })
            {
                var finding = report.Findings.FirstOrDefault(f => f.BlitzCheckId == id);
                if (finding is not null)
                {
                    Assert.False(finding.Unclassified,
                        "CheckID " + id + " fired and rendered as an unclassified check.");
                    Assert.DoesNotContain("sp_Blitz Check", finding.Name, StringComparison.Ordinal);
                    newIdReport.AppendLine(id + ": FIRED, rendered as " + finding.Name);
                }
                else
                {
                    Assert.True(catalog.ContainsKey(id),
                        "CheckID " + id + " did not fire on this instance, and the catalog cannot resolve "
                        + "it by id either, so it would render nameless if it ever did fire.");
                    newIdReport.AppendLine(id + ": did not fire here; catalog resolves it as "
                                           + catalog[id].Name);
                }
            }
            WriteEvidence("new-checkids-273-274-275.txt", newIdReport.ToString());
            Line(newIdReport.ToString().TrimEnd());

            WriteEvidence("report-summary.txt",
                "instance=" + report.Instance + Environment.NewLine
                + "healthScore=" + report.HealthScore.ToString(CultureInfo.InvariantCulture) + Environment.NewLine
                + "universeSize=" + report.UniverseSize + Environment.NewLine
                + "checksFired=" + report.ChecksFired + Environment.NewLine
                + "infoFired=" + report.InfoFired + Environment.NewLine
                + "unclassifiedFired=" + report.UnclassifiedFired + Environment.NewLine
                + "findings=" + report.Findings.Count + Environment.NewLine
                + string.Join(Environment.NewLine,
                    report.Findings.Select(f => "  " + f.BlitzCheckId + "  x" + f.FireCount
                                                + "  " + (f.Unclassified ? "UNCLASSIFIED " : "") + f.Name)));
        }
        finally
        {
            try { Directory.Delete(csvDir, recursive: true); } catch (IOException) { }
        }

        // ---- AFTER snapshot ------------------------------------------------------------------
        var after = ProcedureSnapshot(target);
        WriteEvidence("procedures-after.txt",
            string.Join(Environment.NewLine,
                after.Select(p => p.Name + "\t" + p.Modified.ToString("O", CultureInfo.InvariantCulture))));

        var beforeNames = before.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = after.Select(p => p.Name).Where(n => !beforeNames.Contains(n)).ToList();
        var removed = beforeNames.Where(n => !after.Any(p => n.Equals(p.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        Line("procs after   : " + after.Count + "  added=[" + string.Join(", ", added)
             + "]  removed=[" + string.Join(", ", removed) + "]");
        WriteEvidence("procedures-delta.txt",
            "added: " + string.Join(", ", added) + Environment.NewLine
            + "removed: " + string.Join(", ", removed));

        Assert.Empty(removed);
        Assert.True(after.Any(p => p.Name == "sp_Blitz"));
        Assert.True(after.Any(p => p.Name == "sp_ineachdb"));
    }

    /// <summary>
    /// The version the instance actually got, read back from the installed proc rather than from
    /// the file on disk. A file can say 8.34 while the server still runs whatever was installed
    /// last, which is exactly the provenance trap this house has been burned by.
    /// </summary>
    [LiveFact("FRK_LIVE_TARGET")]
    public void The_installed_proc_reports_the_pinned_version()
    {
        var target = RequireTarget();

        string version, versionDate;
        using (var conn = new SqlConnection(ConnString(target)))
        {
            conn.Open();
            using var cmd = new SqlCommand(
                "DECLARE @v VARCHAR(30), @d DATETIME; "
                + "EXEC dbo.sp_Blitz @VersionCheckMode = 1, @Version = @v OUTPUT, @VersionDate = @d OUTPUT; "
                + "SELECT @v, CONVERT(CHAR(8), @d, 112);", conn) { CommandTimeout = 120 };
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read(), "the version-check call returned no row");
            version = reader.GetString(0);
            versionDate = reader.GetString(1);
        }

        Line("installed sp_Blitz: " + version + " / " + versionDate);
        WriteEvidence("installed-version.txt", version + " / " + versionDate);

        Assert.Equal(FrkContractTests.ExpectedFrkVersion, version);
        Assert.Equal(FrkContractTests.ExpectedFrkVersionDate, versionDate);
    }

    /// <summary>
    /// THE STALE-OUTPUT GUARD, PROVED ON A REAL INSTANCE.
    ///
    /// <para>Until 2026-08-23 an ExecutionParameters failure was a warning: the runner carried on to
    /// SqlQueryForOutput, exported what it found, and set Success = true. The shipped sp_Blitz entry
    /// reads its output table by latest CheckDate, so a server whose EXEC failed exported the
    /// PREVIOUS run's audit under today's file name. This test drives the real runner with an
    /// ExecutionParameters that cannot succeed - a stored procedure that does not exist, which is
    /// exactly the shape of "sp_Blitz 8.34 without its sp_ineachdb prerequisite" - and asserts the
    /// run fails CLOSED.</para>
    ///
    /// <para>The positive control in the same test is what makes the negative one mean anything: the
    /// identical config with a succeeding ExecutionParameters DOES run the output query and DOES
    /// write a CSV, so "no rows, no CSV" is the failure's doing and not the harness's.</para>
    ///
    /// <para>Fixture: installs sp_ineachdb.sql (the same install the app performs, idempotent) and
    /// writes one CSV into the app's own output folder, which it deletes afterwards. It touches no
    /// audit table.</para>
    /// </summary>
    [LiveFact("FRK_LIVE_TARGET")]
    public async Task A_failed_execution_never_exports_the_previous_run()
    {
        var target = RequireTarget();

        var runner = new DiagnosticScriptRunner(
            new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
            NullLogger<DiagnosticScriptRunner>.Instance);

        var connection = new ServerConnection
        {
            ServerNames = target,
            Database = "master",
            UseWindowsAuthentication = true,
            TrustServerCertificate = true,
        };

        // A name that cannot collide with a real script's CSV, so the before/after file census below
        // is measuring only this test.
        const string ProbeName = "FrkStaleOutputProbe";
        var outputDir = Path.Combine(AppContext.BaseDirectory, "output");
        Directory.CreateDirectory(outputDir);

        string[] ProbeCsvs() => Directory.GetFiles(outputDir, "*_" + ProbeName + "_*.csv");

        foreach (var leftover in ProbeCsvs())
        {
            try { File.Delete(leftover); } catch (IOException) { }
        }
        Assert.Empty(ProbeCsvs());

        ScriptConfiguration Probe(string executionParameters) => new()
        {
            Id = ProbeName,
            Name = ProbeName,
            Description = "Stale-output guard probe (test fixture).",
            ScriptPath = "sp_ineachdb.sql",       // a real, idempotent install; the probe is about what follows it
            ExecutionTest = string.Empty,          // so ExecutionParameters always runs
            ExecutionParameters = executionParameters,
            SqlQueryForOutput = "SELECT 1 AS StaleRowMarker;",
            Enabled = true,
            TimeoutSeconds = 60,
            Category = "Diagnostic",
            ExecutionOrder = 99,
            ExportToCsv = true,                    // the old code path would have exported on failure
        };

        // ---- NEGATIVE: the EXEC cannot succeed ------------------------------------------------
        // "Could not find stored procedure" is the same class of failure a missing sp_ineachdb
        // produces inside sp_Blitz 8.34.
        var failed = await runner.ExecuteScriptAsync(
            Probe("EXEC master.dbo.sqltriage_frk_probe_no_such_procedure;"), connection, target);

        Line("fail-closed   : success=" + failed.Success
             + " rows=" + failed.RowsAffected
             + " results=" + (failed.Results is null ? "null" : failed.Results.Count.ToString(CultureInfo.InvariantCulture))
             + " err=" + (failed.ErrorMessage ?? "none"));

        Assert.False(failed.Success,
            "ExecutionParameters threw and the runner still reported Success. That is the defect: for "
            + "the shipped sp_Blitz entry it means the previous audit is exported as this one.");
        Assert.False(string.IsNullOrWhiteSpace(failed.ErrorMessage));
        Assert.Contains("ExecutionParameters failed", failed.ErrorMessage!, StringComparison.Ordinal);
        Assert.True(failed.Results is null || failed.Results.Count == 0,
            "SqlQueryForOutput ran after a failed ExecutionParameters and returned "
            + (failed.Results?.Count ?? 0) + " row(s). Those rows are not this run's.");
        Assert.Equal(0, failed.RowsAffected);
        Assert.Empty(ProbeCsvs());

        // ---- POSITIVE CONTROL: identical config, an EXEC that works ---------------------------
        var ok = await runner.ExecuteScriptAsync(Probe("SELECT 1 AS Ran;"), connection, target);
        Line("positive ctrl : success=" + ok.Success + " rows=" + ok.RowsAffected
             + " err=" + (ok.ErrorMessage ?? "none"));

        Assert.True(ok.Success, "the positive control failed: " + (ok.ErrorMessage ?? "no message"));
        Assert.True(ok.Results is { Count: 1 },
            "the positive control returned " + (ok.Results?.Count ?? 0) + " rows, not the one row "
            + "SqlQueryForOutput selects, so the negative case above proves nothing.");

        var written = ProbeCsvs();
        Assert.True(written.Length == 1,
            "the positive control wrote " + written.Length + " CSVs into " + outputDir
            + "; the whole point of the census is that a successful run DOES write one.");

        WriteEvidence("stale-output-guard.txt",
            "target=" + target + Environment.NewLine
            + "FAILED  : success=" + failed.Success + " rows=" + failed.RowsAffected
            + " csvs=0 err=" + failed.ErrorMessage + Environment.NewLine
            + "CONTROL : success=" + ok.Success + " rows=" + ok.RowsAffected
            + " csvs=1 file=" + Path.GetFileName(written[0]));

        foreach (var f in written)
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }
}
