/* In the name of God, the Merciful, the Compassionate */

// THE LIVE PROOF FOR THE sp_PerfCheck BUNDLE. Everything DarlingContractTests pins is TEXT; this
// file is the only thing that shows the vendored script actually installs and runs, and that the
// rows the app captures are the FINDINGS and not the two-column server banner that precedes them.
//
// It drives the REAL path: the real Config/script-configurations.json entry, the real
// DiagnosticScriptRunner (which installs by splitting on GO, runs SqlSafetyValidator, then runs
// SqlQueryForOutput and advances to OutputResultSetIndex), and the real CSV writer.
//
// WHAT IT PROVES WHEN ARMED
//   1. sp_PerfCheck installs into master from the vendored file and the run reports Success with
//      no error message.
//   2. The procedure on the server stamps itself with the pinned version, read back through its
//      own @version / @version_date OUTPUT parameters under @help = 1.
//   3. The output query really does return TWO result sets, and their shapes are what the config
//      entry assumes: a two-column banner, then the nine-column findings. This is the assertion
//      that makes OutputResultSetIndex = 1 a measured fact rather than a reading of the source.
//   4. What the runner CAPTURED is the findings shape. Delete the NextResult advance and the
//      captured columns become the banner's, because the banner always has at least a Run Date row.
//   5. The app's own CSV writer produces a file for it, under an output root the test controls.
//   6. What a NON-SYSADMIN run does. The design brief read the DECLARE initialisers and predicted a
//      run that SUCCEEDS and is hollow. Measured, it is not: when the caller is not sysadmin the
//      script probes VIEW SERVER STATE for real and keeps the server-state checks, so the run is
//      degraded rather than empty. That is client-facing behaviour, so it is recorded here rather
//      than believed - and the record says which instrument produced it, because on a
//      Windows-auth-only instance the fallback is EXECUTE AS LOGIN impersonation, which is a
//      weaker measurement than a real connection.
//
// INERT unless armed. LiveFactAttribute reports SKIPPED (not passed) when PERFCHECK_LIVE_TARGET is
// unset, and RequireTarget asserts the same variable inside the body so the test FAILS rather than
// passing vacuously if that attribute is ever weakened.
//
// INVOCATION (first armed run 2026-08-24 against .\new2022, SQL 2022 16.0.4262.2):
//   $env:PERFCHECK_LIVE_TARGET = ".\new2022"
//   # SQL Browser is stopped on this box. SqlClient still resolves the named instance; sqlcmd does
//   # not, and needs the shared-memory prefix "lpc:.\new2022" instead.
//   $env:PERFCHECK_LIVE_EVIDENCE_DIR = "C:\temp\perfcheck\live"
//   dotnet test SQLTriage.sln -c Debug --no-build --filter "FullyQualifiedName~PerfCheckLiveSmokeTests"
//
// FIXTURE CONTRACT, and it is a real one.
//   * PERFCHECK_LIVE_TARGET must name a SQL instance the caller is happy to have dbo.sp_PerfCheck
//     INSTALLED INTO MASTER on, because that is exactly what the app does to a client server on
//     every run. Point it at a test instance, never at production.
//   * The script itself persists nothing: every table it builds is a #temp. The only lasting change
//     is the procedure.
//   * The non-sysadmin probe CREATES a SQL login and a master user, and drops both in a finally.
//     If the instance will not take a SQL LOGIN (this box is Windows-auth only, measured
//     2026-08-24), it falls back to EXECUTE AS LOGIN in the admin session and SAYS SO in its
//     result. It never fails the test and it never passes quietly.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public class PerfCheckLiveSmokeTests
{
    private readonly ITestOutputHelper _out;
    public PerfCheckLiveSmokeTests(ITestOutputHelper output) => _out = output;
    private void Line(string s) => _out.WriteLine(s);

    private static string? Target => Environment.GetEnvironmentVariable("PERFCHECK_LIVE_TARGET");
    private static string? EvidenceDir => Environment.GetEnvironmentVariable("PERFCHECK_LIVE_EVIDENCE_DIR");

    /// <summary>
    /// The guard behind <see cref="LiveFactAttribute"/>. If that attribute is ever weakened or
    /// removed, the body must FAIL rather than pass vacuously.
    /// </summary>
    private static string RequireTarget()
    {
        Assert.False(string.IsNullOrWhiteSpace(Target),
            "PERFCHECK_LIVE_TARGET is not set, so this test has no instance to install into and "
            + "nothing to assert. It should have been SKIPPED by LiveFactAttribute; if it ran, that "
            + "attribute is no longer doing its job.");
        return Target!;
    }

    private static string ConnString(string target) =>
        new SqlConnectionStringBuilder
        {
            DataSource = target,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            ApplicationName = "SQLTriage.Tests.PerfCheckLiveSmoke",
        }.ConnectionString;

    private static void WriteEvidence(string fileName, string content)
    {
        var dir = EvidenceDir;
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content, new UTF8Encoding(false));
    }

    private static List<string> ProcedureNames(string target)
    {
        var rows = new List<string>();
        using var conn = new SqlConnection(ConnString(target));
        conn.Open();
        using var cmd = new SqlCommand("SELECT name FROM master.sys.procedures ORDER BY name", conn)
        { CommandTimeout = 60 };
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    private sealed record ResultSetShape(int Ordinal, string[] Columns, int Rows);

    [LiveFact("PERFCHECK_LIVE_TARGET")]
    public async Task sp_PerfCheck_installs_runs_and_the_app_captures_its_findings()
    {
        var target = RequireTarget();

        // CORRECTED 2026-08-24, same as FrkLiveSmokeTests: the runner resolves scripts/ against
        // AppContext.BaseDirectory now, not the working directory. The CWD form was a defect that
        // left the autostart and Windows-service launch vectors unable to find any script at all.
        // Assert the file is beside the binary and say what to do, rather than silently repointing.
        var scriptHere = Path.Combine(AppContext.BaseDirectory, "scripts", "sp_PerfCheck.sql");
        Assert.True(File.Exists(scriptHere),
            "DiagnosticScriptRunner reads scripts/ from AppContext.BaseDirectory, and there is "
            + "no scripts/sp_PerfCheck.sql under " + AppContext.BaseDirectory
            + ". Rebuild so the csproj copy rules put the script beside the test binary.");

        Line("target        : " + target);
        Line("working dir   : " + Directory.GetCurrentDirectory());

        var before = ProcedureNames(target);
        WriteEvidence("procedures-before.txt", string.Join(Environment.NewLine, before));
        Line("procs before  : " + before.Count
             + " (sp_PerfCheck present: " + before.Contains("sp_PerfCheck") + ")");

        // ---- 1. The REAL path ---------------------------------------------------------------
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

        var config = runner.LoadScriptConfigurations()
            .SingleOrDefault(c => c.ScriptPath == DarlingContractTests.ScriptFileName);
        Assert.True(config is not null,
            "no sp_PerfCheck entry in the loaded configuration, so there is nothing to run");
        Assert.Equal(1, config!.OutputResultSetIndex);

        // Whatever the app writes goes under AppContext.BaseDirectory/output. Snapshot it so the
        // file this run produces can be identified and moved somewhere the test owns.
        var appOutputDir = Path.Combine(AppContext.BaseDirectory, "output");
        Directory.CreateDirectory(appOutputDir);
        var filesBefore = Directory.GetFiles(appOutputDir, "*.csv").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wall = Stopwatch.StartNew();
        var result = await runner.ExecuteScriptAsync(config, connection, target);
        wall.Stop();

        Line("run           : success=" + result.Success
             + " rows=" + result.RowsAffected
             + " elapsed=" + result.ExecutionTime.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s"
             + " err=" + (result.ErrorMessage ?? "none"));

        Assert.True(result.Success, "sp_PerfCheck run failed: " + (result.ErrorMessage ?? "no message"));
        Assert.True(string.IsNullOrEmpty(result.ErrorMessage),
            "the run succeeded but the runner recorded a message, which on this path means something "
            + "threw and was tolerated: " + result.ErrorMessage);

        var after = ProcedureNames(target);
        WriteEvidence("procedures-after.txt", string.Join(Environment.NewLine, after));
        Assert.Contains("sp_PerfCheck", after);

        var newProcedures = after.Except(before).ToList();
        Line("new procs     : " + (newProcedures.Count == 0 ? "(none; it was already installed)"
                                                            : string.Join(", ", newProcedures)));

        // ---- 2. The version, read off the SERVER ---------------------------------------------
        // @version and @version_date are assigned before the @help early return, so this reads the
        // stamp out of the INSTALLED procedure without running any of the checks. A file that says
        // 2.8 and a server running something else is the provenance trap the house has been bitten
        // by; this closes it for this script.
        string serverVersion, serverVersionDate;
        using (var conn = new SqlConnection(ConnString(target)))
        {
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "DECLARE @v varchar(30), @d datetime; "
                + "EXEC dbo.sp_PerfCheck @help = 1, @version = @v OUTPUT, @version_date = @d OUTPUT; "
                + "SELECT v = @v, d = CONVERT(char(8), @d, 112);", conn)
            { CommandTimeout = 120 };

            using var reader = await cmd.ExecuteReaderAsync();
            // @help = 1 prints its own result sets first; the SELECT above is the last one.
            string? v = null, d = null;
            do
            {
                if (reader.FieldCount == 2 && reader.GetName(0) == "v" && reader.GetName(1) == "d")
                {
                    Assert.True(await reader.ReadAsync(), "the version SELECT returned no row");
                    v = reader.IsDBNull(0) ? null : reader.GetString(0);
                    d = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            } while (await reader.NextResultAsync());

            serverVersion = v ?? "(null)";
            serverVersionDate = d ?? "(null)";
        }

        Line("server stamp  : " + serverVersion + " / " + serverVersionDate);
        WriteEvidence("server-version.txt", serverVersion + " / " + serverVersionDate);

        Assert.Equal(DarlingContractTests.ExpectedPerfCheckVersion, serverVersion);
        Assert.Equal(DarlingContractTests.ExpectedPerfCheckVersionDate, serverVersionDate);

        // ---- 3. The result-set shapes, measured -----------------------------------------------
        // Run the config entry's own output query directly and walk every set. This is what turns
        // "the source says the findings are set 1" into a fact about this server.
        var shapes = new List<ResultSetShape>();
        var exec = Stopwatch.StartNew();
        using (var conn = new SqlConnection(ConnString(target)))
        {
            await conn.OpenAsync();
            using var cmd = new SqlCommand(config.SqlQueryForOutput, conn)
            { CommandTimeout = config.TimeoutSeconds };

            using var reader = await cmd.ExecuteReaderAsync();
            var ordinal = 0;
            do
            {
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                var rows = 0;
                while (await reader.ReadAsync()) rows++;
                shapes.Add(new ResultSetShape(ordinal++, columns, rows));
            } while (await reader.NextResultAsync());
        }
        exec.Stop();

        WriteEvidence("result-set-shapes.txt", string.Join(Environment.NewLine,
            shapes.Select(s => "set " + s.Ordinal + " (" + s.Rows + " rows): " + string.Join(", ", s.Columns))));
        foreach (var s in shapes)
            Line("set " + s.Ordinal + "        : " + s.Rows + " rows [" + string.Join(", ", s.Columns) + "]");
        Line("EXEC elapsed  : " + exec.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");

        Assert.True(shapes.Count == 2,
            "sp_PerfCheck returned " + shapes.Count + " result sets on this instance, not 2. The "
            + "config entry's OutputResultSetIndex is a fixed number and it now points somewhere "
            + "other than the findings.");
        Assert.Equal(DarlingContractTests.ServerBannerColumns, shapes[0].Columns);
        Assert.Equal(DarlingContractTests.ExpectedFindingsColumns, shapes[1].Columns);
        Assert.True(shapes[0].Rows > 0,
            "the server banner is empty, so capturing set 0 by mistake would look like an empty "
            + "export rather than a wrong one, and the assertion below would prove less than it says");

        // ---- 4. What the RUNNER captured is the findings shape --------------------------------
        var capturedColumns = result.Results is { Count: > 0 }
            ? result.Results[0].Keys.ToArray()
            : Array.Empty<string>();

        WriteEvidence("captured-header.txt", string.Join(", ", capturedColumns));

        if (result.RowsAffected > 0)
        {
            Assert.Equal(DarlingContractTests.ExpectedFindingsColumns, capturedColumns);
            Assert.NotEqual(DarlingContractTests.ServerBannerColumns, capturedColumns);
            Assert.Equal(shapes[1].Rows, result.RowsAffected);
        }
        else
        {
            // A findings set can legitimately be empty; the banner never is. So zero captured rows
            // is itself proof that the reader was NOT sitting on set 0.
            Assert.True(shapes[1].Rows == 0,
                "the runner captured nothing while the findings set holds " + shapes[1].Rows
                + " rows, so it read a different set from the one this test measured.");
            Line("note          : this instance produced no findings, so the captured HEADER could "
                 + "not be checked. Zero rows still proves set 0 was skipped: the banner always has rows.");
        }

        // ---- 4b. The offline extraction knows every id this instance really fired --------------
        // DarlingContractTests.EmittedCheckIds reads the script as TEXT, and a textual extraction
        // can miss a whole emission form without anything going red. It did: the first cut knew only
        // the "check_id = N" form and reported 31 ids, and this assertion is what caught it, because
        // a real run fired 1003, 5000 and 6002 from the VALUES form. The two instruments are wired
        // together on purpose.
        var firedIds = (result.Results ?? new List<Dictionary<string, object>>())
            .Where(r => r.TryGetValue("check_id", out var v) && v is not null)
            .Select(r => Convert.ToInt32(r["check_id"], CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        WriteEvidence("fired-check-ids.txt", string.Join(", ", firedIds));
        Line("fired ids     : " + string.Join(", ", firedIds));

        var textualSet = DarlingContractTests.EmittedCheckIds().ToHashSet();
        var unknown = firedIds.Where(id => !textualSet.Contains(id)).ToList();
        Assert.True(unknown.Count == 0,
            "this instance emitted check_ids that DarlingContractTests.EmittedCheckIds does not "
            + "contain: " + string.Join(", ", unknown)
            + ". The textual extraction is missing an emission form. Widen it; do not add the ids "
            + "by hand.");

        // ---- 5. The app's own CSV -------------------------------------------------------------
        var written = Directory.GetFiles(appOutputDir, "*.csv")
            .Where(f => !filesBefore.Contains(f))
            .ToList();

        var outputRoot = Path.Combine(Path.GetTempPath(), "perfcheck-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputRoot);
        try
        {
            if (result.RowsAffected > 0)
            {
                Assert.True(written.Count == 1,
                    "the run captured " + result.RowsAffected + " rows and the app wrote "
                    + written.Count + " new CSV files into " + appOutputDir);
                Assert.Contains("_sp_PerfCheck_", Path.GetFileName(written[0]), StringComparison.Ordinal);

                var moved = Path.Combine(outputRoot, Path.GetFileName(written[0]));
                File.Move(written[0], moved);

                var csv = File.ReadAllText(moved);
                var header = csv.Split('\n')[0].Trim();
                Line("csv           : " + Path.GetFileName(moved));
                Line("csv header    : " + header);
                WriteEvidence("app-written-sp_PerfCheck.csv", csv);

                // ExportToCsv prepends ServerName, then the captured columns in order.
                Assert.StartsWith("\"ServerName\",\"check_id\",\"priority\"", header, StringComparison.Ordinal);
                Assert.DoesNotContain("Server Information", header, StringComparison.Ordinal);
            }
            else
            {
                // ExportScriptToCsv returns early on zero rows, by design and unchanged by this lane.
                Assert.True(written.Count == 0,
                    "no rows were captured and yet a CSV appeared: " + string.Join(", ", written));
                Line("csv           : none (no findings on this instance)");
            }
        }
        finally
        {
            try { Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }

        // ---- 6. What a NON-SYSADMIN run actually does -----------------------------------------
        var nonSysadmin = await ProbeNonSysadminAsync(target);
        Line("non-sysadmin  : " + nonSysadmin);
        WriteEvidence("non-sysadmin-probe.txt", nonSysadmin);

        WriteEvidence("summary.txt", string.Join(Environment.NewLine, new[]
        {
            "target            : " + target,
            "server version    : " + serverVersion + " / " + serverVersionDate,
            "runner success    : " + result.Success,
            "runner rows       : " + result.RowsAffected,
            "runner elapsed s  : " + result.ExecutionTime.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture),
            "bare EXEC s       : " + exec.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture),
            "result sets       : " + shapes.Count,
            "set 0             : " + string.Join(", ", shapes[0].Columns) + "  (" + shapes[0].Rows + " rows)",
            "set 1             : " + string.Join(", ", shapes[1].Columns) + "  (" + shapes[1].Rows + " rows)",
            "captured header   : " + string.Join(", ", capturedColumns),
            "non-sysadmin      : " + nonSysadmin,
        }));
    }

    /// <summary>
    /// Creates a throwaway SQL login with VIEW SERVER STATE and nothing else, runs sp_PerfCheck as
    /// it, and reports what came back. Drops the login and its master user in a finally.
    ///
    /// <para>This is a RECORDING, not an assertion. What a degraded run looks like is a fact about
    /// the third-party script, and the point of measuring it is that the config entry's Description
    /// tells the operator the truth. If the instance will not take a SQL login, that is said out
    /// loud instead of quietly passing.</para>
    /// </summary>
    private static async Task<string> ProbeNonSysadminAsync(string target)
    {
        var loginName = "sqltriage_perfcheck_probe_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var password = "Pc!" + Guid.NewGuid().ToString("N") + "aA1";

        try
        {
            using var admin = new SqlConnection(ConnString(target));
            await admin.OpenAsync();

            try
            {
                using var create = new SqlCommand(
                    "CREATE LOGIN " + Quote(loginName) + " WITH PASSWORD = " + Literal(password)
                    + ", CHECK_POLICY = OFF;"
                    + "CREATE USER " + Quote(loginName) + " FOR LOGIN " + Quote(loginName) + ";"
                    + "GRANT VIEW SERVER STATE TO " + Quote(loginName) + ";"
                    + "GRANT EXECUTE ON dbo.sp_PerfCheck TO " + Quote(loginName) + ";", admin)
                { CommandTimeout = 60 };
                await create.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                return "NOT RUN: could not create a throwaway login on this instance (" + ex.Message
                       + "). The degraded-permissions behaviour is untested here.";
            }

            var probeConnString = new SqlConnectionStringBuilder(ConnString(target))
            {
                IntegratedSecurity = false,
                UserID = loginName,
                Password = password,
            }.ConnectionString;

            try
            {
                using var probe = new SqlConnection(probeConnString);
                await probe.OpenAsync();

                using var cmd = new SqlCommand("EXEC dbo.sp_PerfCheck;", probe) { CommandTimeout = 900 };
                using var reader = await cmd.ExecuteReaderAsync();

                return "RAN as a real VIEW SERVER STATE login (not sysadmin): "
                       + await DescribeDegradedRunAsync(reader);
            }
            catch (Exception ex)
            {
                // Mixed-mode authentication is off on this box, so a SQL login cannot connect at
                // all. Fall back to impersonating it inside the admin session: EXECUTE AS LOGIN
                // gives the same server-level security context, and IS_SRVROLEMEMBER('sysadmin')
                // (which is the whole gate inside sp_PerfCheck) answers 0 under it. Labelled
                // separately because impersonation is not a login: an impersonated session is
                // sandboxed away from other databases unless the database is TRUSTWORTHY, so a
                // per-database check can fail here for a reason a real login would not hit.
                var impersonated = await ImpersonateAsync(target, loginName);
                return "SQL login could not connect (" + ex.Message + "). " + impersonated;
            }
            finally
            {
                SqlConnection.ClearAllPools();
            }
        }
        catch (Exception ex)
        {
            return "NOT RUN: " + ex.Message;
        }
        finally
        {
            try
            {
                using var admin = new SqlConnection(ConnString(target));
                admin.Open();
                using var drop = new SqlCommand(
                    "IF DATABASE_PRINCIPAL_ID(" + Literal(loginName) + ") IS NOT NULL DROP USER "
                    + Quote(loginName) + ";"
                    + "IF SUSER_ID(" + Literal(loginName) + ") IS NOT NULL DROP LOGIN "
                    + Quote(loginName) + ";", admin)
                { CommandTimeout = 60 };
                drop.ExecuteNonQuery();
            }
            catch { /* best effort: the name is a GUID, so a leftover is inert and identifiable */ }
        }
    }

    /// <summary>
    /// The fallback probe: run sp_PerfCheck under EXECUTE AS LOGIN in the admin session. Reports
    /// what came back; never throws.
    /// </summary>
    private static async Task<string> ImpersonateAsync(string target, string loginName)
    {
        try
        {
            using var conn = new SqlConnection(ConnString(target));
            await conn.OpenAsync();

            using (var check = new SqlCommand(
                       "EXECUTE AS LOGIN = " + Literal(loginName) + "; "
                       + "SELECT sysadmin = IS_SRVROLEMEMBER('sysadmin'), "
                       + "vss = HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE');", conn)
                   { CommandTimeout = 60 })
            {
                using var r = await check.ExecuteReaderAsync();
                Assert.True(await r.ReadAsync(), "the impersonation context check returned no row");
                var sysadmin = r.IsDBNull(0) ? -1 : Convert.ToInt32(r.GetValue(0));
                var vss = r.IsDBNull(1) ? -1 : Convert.ToInt32(r.GetValue(1));
                if (sysadmin != 0)
                    return "IMPERSONATION PROBE ABANDONED: the impersonated context still reports "
                           + "sysadmin = " + sysadmin + ", so it would prove nothing.";
                if (vss != 1)
                    return "IMPERSONATION PROBE ABANDONED: the impersonated context does not hold "
                           + "VIEW SERVER STATE (" + vss + ").";
            }

            using var cmd = new SqlCommand(
                "EXECUTE AS LOGIN = " + Literal(loginName) + "; EXEC dbo.sp_PerfCheck; REVERT;", conn)
            { CommandTimeout = 900 };

            using var reader = await cmd.ExecuteReaderAsync();
            return "IMPERSONATION PROBE (EXECUTE AS LOGIN, not a real connection), sysadmin = 0 and "
                   + "VIEW SERVER STATE held: " + await DescribeDegradedRunAsync(reader);
        }
        catch (Exception ex)
        {
            return "IMPERSONATION PROBE FAILED: " + ex.Message
                   + ". The degraded-permissions behaviour is UNTESTED on this instance.";
        }
    }

    /// <summary>
    /// Walks every result set of a degraded run and says what came back, PER SET.
    ///
    /// <para>Per set matters and the first draft of this probe got it wrong. sp_PerfCheck writes
    /// its "Information unavailable (requires VIEW SERVER STATE permission)" notes into
    /// #server_info, which is result set 0 - the banner the app does NOT export. Counting them in
    /// the findings alone returned zero and read as "no degradation", when the run had in fact lost
    /// most of its checks.</para>
    /// </summary>
    private static async Task<string> DescribeDegradedRunAsync(Microsoft.Data.SqlClient.SqlDataReader reader)
    {
        var perSet = new List<string>();
        var ordinal = 0;
        do
        {
            var rows = 0;
            var unavailable = 0;
            while (await reader.ReadAsync())
            {
                rows++;
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.IsDBNull(i)) continue;
                    if (reader.GetValue(i) is string s && s.Contains("Information unavailable",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        unavailable++;
                        break;
                    }
                }
            }
            perSet.Add("set " + ordinal + ": " + rows + " rows, " + unavailable
                       + " 'Information unavailable'");
            ordinal++;
        } while (await reader.NextResultAsync());

        return "the EXEC SUCCEEDED. " + string.Join("; ", perSet)
               + ". The run does not fail and the CSV is still written; the permission notes land in "
               + "set 0, which the app does not export.";
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
}
