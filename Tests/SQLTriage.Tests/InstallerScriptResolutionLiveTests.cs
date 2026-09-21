/* In the name of God, the Merciful, the Compassionate */

// THE LIVE PROOF THAT AN INSTALLED BUILD CAN FIND ITS OWN DIAGNOSTIC SCRIPTS.
//
// WHY THIS FILE EXISTS. Two independent defects were proved on 2026-08-24 and both are fixed in the
// same commit range as this harness:
//
//   1. installer/SQLTriage.iss shipped no scripts\ entry, since the installer was introduced. An
//      installed build had no scripts\ folder at all. ScriptCensusTests carries the text tripwire
//      for that half; PROBE A (C:\temp\installer-lane) is the compiled-and-installed measurement.
//   2. DiagnosticScriptRunner resolved the folder as Path.Combine("scripts", ScriptPath), i.e.
//      against the CURRENT WORKING DIRECTORY. The autostart Run key and the Windows service both
//      start the process with a working directory of C:\Windows\System32, so shipping the folder
//      alone would still have left those two launch vectors dead. This file is the measurement for
//      that half, and it is a runtime fact that no text census can reach.
//
// WHAT IT PROVES WHEN ARMED
//   1. With a working directory that contains NO scripts\ folder, the real DiagnosticScriptRunner
//      still loads the real scripts\sp_Blitz.sql and reports Success. That is the installed-service
//      shape, and before the fix it returned Success = false with "Script not found:
//      scripts\sp_Blitz.sql".
//   2. NON-VACUITY. From the same alien working directory, an entry naming a script that does not
//      exist still fails, and still fails with the "Script not found" wording. Without this arm a
//      harness that silently stopped exercising the runner would report green.
//
//      IT IS ITS OWN TEST, DELIBERATELY, and that was a correction. Both arms first lived in one
//      method, arm 1 before arm 2. On the pre-fix measurement of 2026-08-24 arm 1's assertion threw,
//      xunit stopped the method there, and the control never executed - the run reported Total: 1.
//      A control that only runs when the subject already passed cannot tell you the harness was
//      working on the run where the subject failed, which is the one run you most need it for. Split
//      into two [LiveFact]s, both arms are measured on every run, in both directions.
//   3. The folder the runner READS is the folder AutoUpdateService WRITES (AutoUpdateService.cs
//      :791 and :842 both create AppContext.BaseDirectory\scripts). Before the fix the updater
//      self-healed a directory the runner never looked in.
//
// INERT unless armed. LiveFactAttribute reports SKIPPED (not passed) when INSTALLER_PROBE_TARGET is
// unset, and RequireTarget asserts the same variable inside the body so the test FAILS rather than
// passing vacuously if that attribute is ever weakened.
//
// INVOCATION (first armed run 2026-08-24 against localhost\NEW2022, SQL 2022 16.0.4262.2):
//   $env:INSTALLER_PROBE_TARGET = "localhost\NEW2022"
//   $env:INSTALLER_PROBE_EVIDENCE_DIR = "C:\temp\installer-lane\probeB"
//   dotnet test SQLTriage.sln -c Debug --no-build --filter "FullyQualifiedName~InstallerScriptResolutionLiveTests"
//
// FIXTURE CONTRACT, and it is a real one.
//   * INSTALLER_PROBE_TARGET must name a SQL instance the caller is happy to have dbo.sp_Blitz
//     INSTALLED INTO MASTER on, and to have master.dbo.sqldba_sp_Blitz_output written, because that
//     is what the shipped configuration entry does. Point it at a test instance, never production.
//     This is the same fixture the FRK and sp_PerfCheck live harnesses already use.
//   * The body MUTATES THE PROCESS WORKING DIRECTORY. Directory.SetCurrentDirectory is
//     process-global, so the class sits in its own collection with parallelisation disabled, and
//     the original directory is restored in a finally. The assembly's xunit.runner.json also sets
//     parallelizeTestCollections=false / maxParallelThreads=1; the collection attribute is the
//     belt to that braces, because a future runner-config change must not silently re-arm the
//     hazard.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// Collection for harnesses that mutate process-global state (here: the current working directory).
/// Parallelisation is disabled so a sibling test cannot observe the mutated directory.
/// </summary>
[CollectionDefinition("process-cwd-mutating", DisableParallelization = true)]
public sealed class ProcessCwdMutatingCollection { }

[Collection("process-cwd-mutating")]
public class InstallerScriptResolutionLiveTests
{
    private readonly ITestOutputHelper _out;
    public InstallerScriptResolutionLiveTests(ITestOutputHelper output) => _out = output;
    private void Line(string s) => _out.WriteLine(s);

    private static string? Target => Environment.GetEnvironmentVariable("INSTALLER_PROBE_TARGET");
    private static string? EvidenceDir => Environment.GetEnvironmentVariable("INSTALLER_PROBE_EVIDENCE_DIR");

    /// <summary>
    /// The guard behind <see cref="LiveFactAttribute"/>. If that attribute is ever weakened or
    /// removed, the body must FAIL rather than pass vacuously.
    /// </summary>
    private static string RequireTarget()
    {
        Assert.False(string.IsNullOrWhiteSpace(Target),
            "INSTALLER_PROBE_TARGET is not set, so this test has no instance to run a diagnostic "
            + "script against and nothing to assert. It should have been SKIPPED by "
            + "LiveFactAttribute; if it ran, that attribute is no longer doing its job.");
        return Target!;
    }

    private static void WriteEvidence(string fileName, string content)
    {
        var dir = EvidenceDir;
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content, new UTF8Encoding(false));
    }

    /// <summary>
    /// The shared fixture for both arms: a real runner, a real connection, the shipped sp_Blitz
    /// entry, and a freshly created working directory that contains no scripts folder. The body
    /// runs with the process working directory set to that alien directory, and it is restored in a
    /// finally whatever the body does.
    /// </summary>
    private async Task InAnAlienWorkingDirectory(
        string evidenceFileName,
        Func<DiagnosticScriptRunner, ServerConnection, ScriptConfiguration, string, Action<string>, Task> body)
    {
        var target = RequireTarget();
        var original = Directory.GetCurrentDirectory();
        var alienCwd = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "sqlt-cwd-probe-" + Guid.NewGuid().ToString("N"))).FullName;

        var log = new StringBuilder();
        void Record(string s) { Line(s); log.AppendLine(s); }

        Record("target          : " + target);
        Record("original cwd    : " + original);
        Record("alien cwd       : " + alienCwd);
        Record("base directory  : " + AppContext.BaseDirectory);

        // The precondition that makes an alien-cwd arm meaningful: the script really is beside the
        // binary, and really is NOT under the alien working directory.
        var besideBinary = Path.Combine(AppContext.BaseDirectory, "scripts", "sp_Blitz.sql");
        Assert.True(File.Exists(besideBinary),
            "scripts/sp_Blitz.sql is not beside the test binary at " + besideBinary
            + ", so this harness cannot tell a working fix from a missing file. Rebuild.");
        Assert.False(Directory.Exists(Path.Combine(alienCwd, "scripts")),
            "the freshly created alien working directory already has a scripts folder");
        Record("beside binary   : " + besideBinary);

        // The updater writes the folder the runner reads. Before the fix these were different
        // directories on every launch vector whose working directory is not the install folder.
        var updaterWrites = Path.Combine(AppContext.BaseDirectory, "scripts");
        Assert.Equal(
            Path.GetFullPath(updaterWrites).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Path.GetDirectoryName(besideBinary)!).TrimEnd(Path.DirectorySeparatorChar));
        Record("updater writes  : " + updaterWrites + "  (AutoUpdateService.cs:791, :842)");

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

        var config = runner.LoadScriptConfigurations().SingleOrDefault(c => c.ScriptPath == "sp_Blitz.sql");
        Assert.True(config is not null,
            "no sp_Blitz entry in the loaded configuration, so there is nothing to run");

        try
        {
            Directory.SetCurrentDirectory(alienCwd);
            Record("cwd during run  : " + Directory.GetCurrentDirectory());

            await body(runner, connection, config!, target, Record);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            try { Directory.Delete(alienCwd, true); } catch (IOException) { }
            WriteEvidence(evidenceFileName, log.ToString());
        }
    }

    [LiveFact("INSTALLER_PROBE_TARGET")]
    public async Task The_runner_finds_its_scripts_from_a_working_directory_that_has_none()
    {
        // THE INSTALLED-SERVICE SHAPE. The autostart Run key and the SCM both start the process with
        // a working directory of C:\Windows\System32. Before 2026-08-24 this returned Success=false
        // with "Script not found: scripts\sp_Blitz.sql".
        await InAnAlienWorkingDirectory("script-resolution-arm1-real.txt",
            async (runner, connection, config, target, Record) =>
            {
                var result = await runner.ExecuteScriptAsync(config, connection, target);
                Record("arm 1 (real)    : success=" + result.Success
                       + " rows=" + result.RowsAffected
                       + " elapsed=" + result.ExecutionTime.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s"
                       + " err=" + (result.ErrorMessage ?? "none"));

                Assert.True(result.Success,
                    "DiagnosticScriptRunner could not run sp_Blitz from a working directory with no "
                    + "scripts folder. That is the autostart-and-service shape: the Run key and the "
                    + "SCM both start the process with a working directory of C:\\Windows\\System32. "
                    + "The script must resolve against AppContext.BaseDirectory. Error: "
                    + (result.ErrorMessage ?? "none"));
                Assert.DoesNotContain("Script not found", result.ErrorMessage ?? "");
            });
    }

    [LiveFact("INSTALLER_PROBE_TARGET")]
    public async Task The_runner_still_reports_a_script_it_cannot_find()
    {
        // NON-VACUITY, AND IT IS A SEPARATE TEST ON PURPOSE. A harness that stopped exercising the
        // file-resolution path altogether would pass the arm above forever. Running as its own
        // [LiveFact] means this control is measured on the runs where the subject FAILS too, which
        // is the run it is actually needed on: when both arms lived in one method, the pre-fix
        // measurement stopped at the first assertion and the control never executed.
        await InAnAlienWorkingDirectory("script-resolution-arm2-control.txt",
            async (runner, connection, config, target, Record) =>
            {
                var absent = CloneWithScriptPath(
                    config, "no_such_script_" + Guid.NewGuid().ToString("N") + ".sql");
                var result = await runner.ExecuteScriptAsync(absent, connection, target);
                Record("arm 2 (control) : success=" + result.Success
                       + " err=" + (result.ErrorMessage ?? "none"));

                Assert.False(result.Success,
                    "a configuration entry naming a script that does not exist reported Success, so "
                    + "this harness can no longer tell a resolved script from an unresolved one");
                Assert.Contains("Script not found", result.ErrorMessage ?? "");
            });
    }

    /// <summary>
    /// A copy of a shipped configuration entry with a different ScriptPath, so the control arm
    /// exercises the SAME code path as arm 1 and differs only in the file it names.
    /// </summary>
    private static ScriptConfiguration CloneWithScriptPath(ScriptConfiguration source, string scriptPath) =>
        new()
        {
            Id = source.Id,
            Name = source.Name + " (missing-file control)",
            Description = source.Description,
            ScriptPath = scriptPath,
            ExecutionTest = source.ExecutionTest,
            ExecutionParameters = source.ExecutionParameters,
            SqlQueryForOutput = source.SqlQueryForOutput,
            Enabled = source.Enabled,
            TimeoutSeconds = source.TimeoutSeconds,
            Category = source.Category,
            ExecutionOrder = source.ExecutionOrder,
            ExportToCsv = false,
            OutputResultSetIndex = source.OutputResultSetIndex,
            EmptyResultIsNormal = source.EmptyResultIsNormal,
        };
}
