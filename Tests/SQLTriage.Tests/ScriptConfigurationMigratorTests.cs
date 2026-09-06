/* In the name of God, the Merciful, the Compassionate */

// -- Ruling 2026-08-23 #4: an upgraded install never receives new shipped script entries -------
//
// WHY THIS FILE EXISTS. installer/SQLTriage.iss:100 ships Config/script-configurations.json with
//
//     Flags: onlyifdoesntexist
//
// which is right for the operator's edits and wrong for new scripts. The sp_Blitz 8.34 refresh
// added an install-only sp_ineachdb entry -- 8.34 hard-depends on dbo.sp_ineachdb, which replaced
// sp_MSforeachdb upstream -- and an Inno UPGRADE keeps the old file, so an upgraded install would
// run sp_Blitz 8.34 with the prerequisite missing. Adrian's ruling, DECISIONS 2026-08-23 05:00 #4:
// at startup, add any shipped script entries missing from the installed config; never overwrite
// operator edits. The installer flag is deliberately UNCHANGED -- operator edits still survive an
// upgrade, and the app fills the gap instead.
//
// WHAT IS REAL HERE. OldShapeInstalledConfig below is the LITERAL Config/script-configurations.json
// as it stood at dev main 8ff4cac, taken from that commit: four entries and no sp_ineachdb.
// Every merge below runs the production ScriptConfigurationMigrator against it, and the
// end-to-end test drives the real DiagnosticScriptRunner.LoadScriptConfigurations. Nothing is
// re-implemented in the test.
//
// MUTATIONS THAT MUST FAIL:
//   1. Drop the merge -- delete the ScriptConfigurationMigrator.EnsureShippedEntries call from
//      DiagnosticScriptRunner.LoadScriptConfigurations, or make TryMerge always return false.
//   2. Make the merge overwrite -- rebuild the file from the shipped defaults, or serialise the
//      merged list back through JsonSerializer instead of splicing text.
// Measured 2026-08-23. Mutation 1 turns
// LoadScriptConfigurations_adds_a_missing_shipped_entry_before_it_reads red, and nothing else.
// Mutation 2 first SURVIVED all fifteen tests, because every "operator edits survive" assertion
// ran against the pure TryMerge and none against the half that writes;
// An_operator_edited_file_on_disk_is_added_to_and_never_replaced was added for exactly that and
// is what turns red now.
//   3. Restore the old comma guard -- "if (installed.Count > 0 || builder.Length > 1)" in
//      TryMerge. Turns the empty-array theory red on every shape with leading whitespace.
//   4. Write with new UTF8Encoding(false) instead of the detected encoding. Turns
//      A_byte_order_mark_the_operator_had_is_still_there_afterwards red.
//   5. Stop growing the have-sets inside the shipped loop. Turns
//      A_shipped_file_that_names_one_script_twice_adds_it_once red.
// Mutations 3-5 measured 2026-08-23 in the fix pass; logs under C:\temp\small2\mutations.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;

// SQLTriage.Data also defines a LogLevel, so the logging one is named explicitly here.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SQLTriage.Tests;

public sealed class ScriptConfigurationMigratorTests : IDisposable
{
    private readonly string _dir;

    public ScriptConfigurationMigratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "script-cfg-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Config/script-configurations.json exactly as dev main 8ff4cac shipped it: the four entries
    /// an install from that build has on disk, and no sp_ineachdb. This is the file the ruling is
    /// about, not a fixture shaped like it.
    /// </summary>
    private const string OldShapeInstalledConfig = """
    [
      {
        "Id": "107152ff-bf0b-4f4f-b310-3f385e395355",
        "Name": "sp_Blitz",
        "Description": "Diagnostic script: sp_Blitz.sql",
        "ScriptPath": "sp_Blitz.sql",
        "ExecutionTest": "IF OBJECT_ID(\u0027master.dbo.sqldba_sp_Blitz_output\u0027) IS NOT NULL\nBEGIN\n\tSELECT CASE WHEN (SELECT MAX([CheckDate]) FROM master.dbo.sqldba_sp_Blitz_output) \u003E DATEADD(DAY,-1,GETDATE()) THEN 0 ELSE 1 END [ToRun]\nEND\nELSE\nBEGIN\n\tSELECT 1 [ToRun]\nEND",
        "ExecutionParameters": "EXEC [dbo].[sp_Blitz] @CheckUserDatabaseObjects = 1 , @CheckProcedureCache = 1 , @OutputType = \u0027TABLE\u0027 , @OutputProcedureCache = 0 , @CheckProcedureCacheFilter = NULL, @CheckServerInfo = 1, @OutputDatabaseName = \u0027master\u0027, @OutputSchemaName = \u0027dbo\u0027, @OutputTableName = \u0027sqldba_sp_Blitz_output\u0027, @BringThePain = 1;",
        "SqlQueryForOutput": "DECLARE @ThisDomain NVARCHAR(100);\nEXEC master.dbo.xp_regread \u0027HKEY_LOCAL_MACHINE\u0027, \u0027SYSTEM\\CurrentControlSet\\services\\Tcpip\\Parameters\u0027, N\u0027Domain\u0027,@ThisDomain OUTPUT;SET @ThisDomain = ISNULL(@ThisDomain, DEFAULT_DOMAIN());\nSELECT  ID, @ThisDomain [Domain],ServerName, CONVERT(VARCHAR,CheckDate,120), Priority, FindingsGroup, Finding, DatabaseName, URL, Details, CheckID FROM master.dbo.sqldba_sp_Blitz_output WHERE CheckDate = (SELEct max([CheckDate]) FROM master.dbo.sqldba_sp_Blitz_output);",
        "Enabled": true,
        "TimeoutSeconds": 900,
        "Category": "Diagnostic",
        "ExecutionOrder": 1,
        "ExportToCsv": true
      },
      {
        "Id": "c307d95e-109f-4d6f-b32d-a972c9c412ec",
        "Name": "Check_BP_Servers",
        "Description": "Diagnostic script: Check_BP_Servers.sql",
        "ScriptPath": "Check_BP_Servers.sql",
        "ExecutionTest": "",
        "ExecutionParameters": "",
        "SqlQueryForOutput": "",
        "Enabled": false,
        "TimeoutSeconds": 900,
        "Category": "Diagnostic",
        "ExecutionOrder": 3,
        "ExportToCsv": true
      },
      {
        "Id": "c5e50737-83ae-40a2-92b8-8f0b7ee9a724",
        "Name": "stpChecklist_Seguranca",
        "Description": "Diagnostic script: stpChecklist_Seguranca.sql",
        "ScriptPath": "stpChecklist_Seguranca.sql",
        "ExecutionTest": "IF OBJECT_ID(\u0027master.dbo.stpSecurity_Checklist_Table\u0027) IS NOT NULL\nBEGIN\n\tSELECT CASE WHEN (SELECT MAX([evaldate]) FROM [master].[dbo].[stpSecurity_Checklist_Table]) \u003E DATEADD(DAY,-1,GETDATE()) THEN 0 ELSE 1 END [ToRun]\nEND\nELSE\nBEGIN\n\tSELECT 1 [ToRun]\nEND\n",
        "ExecutionParameters": "EXEC dbo.stpSecurity_Checklist",
        "SqlQueryForOutput": "SELECT [evaldate]\r\n      ,[Domain]\r\n      ,[SQLInstance]\r\n      ,[code]\r\n      ,[Category]\r\n      ,[Title]\r\n      ,[Result]\r\n      ,[How this can be an Issue]\r\n      ,[Technical explanation]\r\n      ,[How to Fix]\r\n      ,[Result Details]\r\n      ,[External Reference]\r\n  FROM [master].[dbo].[stpSecurity_Checklist_Table]\r\n  WHERE [evaldate] = (SELECT MAX([evaldate]) FROM [master].[dbo].[stpSecurity_Checklist_Table])",
        "Enabled": true,
        "TimeoutSeconds": 900,
        "Category": "Diagnostic",
        "ExecutionOrder": 4,
        "ExportToCsv": true
      },
      {
        "Id": "b55522c5-2c13-4ffd-9399-a0d7104cc00e",
        "Name": "SQLDBA.ORG.sp_triage",
        "Description": "Diagnostic script: SQLDBA.ORG.sp_triage.sql",
        "ScriptPath": "SQLDBA.ORG.sp_triage.sql",
        "ExecutionTest": "IF OBJECT_ID(\u0027master.dbo.[sqldba_sp_triage\u00AE_output]\u0027) IS NOT NULL\nBEGIN\n\tSELECT CASE WHEN (SELECT MAX(evaldate) AS evaldate FROM master.dbo.[sqldba_sp_triage\u00AE_output]) \u003E DATEADD(DAY,-1,GETDATE()) THEN 0 ELSE 1 END [ToRun]\nEND\nELSE\nBEGIN\n\tSELECT 1 [ToRun]\nEND\n",
        "ExecutionParameters": "EXEC  [dbo].[sp_triage\u00AE]",
        "SqlQueryForOutput": "\r\nSELECT TOP (100) PERCENT \r\nID, evaldate, domain, SQLInstance, SectionID, Section, Summary, Severity, Details, HoursToResolveWithTesting, QueryPlan\r\n\r\nFROM \r\n (SELECT         CONVERT(NVARCHAR(25), T1.ID) AS ID, REPLACE(T1.evaldate, \u0027~\u0027, \u0027-\u0027) AS evaldate, REPLACE(T1.domain, \u0027~\u0027, \u0027-\u0027) AS domain, REPLACE(T1.SQLInstance, \u0027~\u0027, \u0027-\u0027) \r\nAS SQLInstance, REPLACE(CONVERT(NVARCHAR(10), T1.SectionID), \u0027~\u0027, \u0027-\u0027) AS SectionID, REPLACE(T1.Section, \u0027~\u0027, \u0027-\u0027) AS Section, REPLACE(T1.Summary, \u0027~\u0027, \u0027-\u0027) AS Summary, \r\n REPLACE(T1.Severity, \u0027~\u0027, \u0027-\u0027) AS Severity, REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(REPLACE(T1.Details, \u0027~\u0027, \u0027-\u0027), N\u0027\u0027), CHAR(9), \u0027 \u0027), CHAR(10), \u0027 \u0027), CHAR(13), \u0027 \u0027), \u0027 \u0027, \u0027 \u0027) AS Details, \r\n REPLACE(CONVERT(NVARCHAR(10), T1.HoursToResolveWithTesting), \u0027~\u0027, \u0027-\u0027) AS HoursToResolveWithTesting, REPLACE(T1.QueryPlan, \u0027~\u0027, \u0027-\u0027) AS QueryPlan, T1.ID AS Sorter\r\nFROM            master.dbo.[sqldba_sp_triage\u00AE_output] AS T1 INNER JOIN\r\n(SELECT        MAX(evaldate) AS evaldate\r\nFROM            master.dbo.[sqldba_sp_triage\u00AE_output]) AS T2 ON T1.evaldate = T2.evaldate) AS T3\r\nORDER BY Sorter ASC\r\n\r\n",
        "Enabled": true,
        "TimeoutSeconds": 900,
        "Category": "Diagnostic",
        "ExecutionOrder": 99,
        "ExportToCsv": true
      }
    ]
    """;

    private static string Shipped()
    {
        var shipped = ScriptConfigurationMigrator.ReadShippedDefaults();
        shipped.Should().NotBeNull(
            "the shipped defaults are an EmbeddedResource in SQLTriage.csproj and every test below "
            + "is meaningless without them");
        return shipped!;
    }

    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    // -- (d) the two copies of the shipped defaults cannot drift ---------------------------------

    /// <summary>
    /// Config\script-configurations.json is BOTH Content (copied to config/ beside the binary, and
    /// shipped by the installer) and EmbeddedResource (the migrator's source of truth). They come
    /// from one file in the repo, and this is the guard that keeps it that way: if someone ever
    /// gives the embedded copy its own path, the merge would start adding entries the installed
    /// build does not have scripts for.
    /// </summary>
    [Fact]
    public void The_embedded_shipped_defaults_are_byte_identical_to_the_config_file_beside_the_binary()
    {
        var embedded = ScriptConfigurationMigrator.ReadShippedDefaultBytes();
        embedded.Should().NotBeNull("SQLTriage.csproj must embed Config\\script-configurations.json");

        var onDisk = ShippedConfigFileBesideTheBinary();
        var fromFile = File.ReadAllBytes(onDisk);

        embedded!.Should().Equal(fromFile,
            "the embedded defaults and the shipped file are the same repo file and must stay so");
    }

    /// <summary>
    /// The shipped entries the 8ff4cac fixture does NOT have, in the order the merge appends them
    /// (shipped-file order). sp_ineachdb arrived with the First Responder Kit 8.34 refresh;
    /// sp_PerfCheck with the Darling Data bundle on 2026-08-24. Each is exactly the case the ruling
    /// is about: a script added to the product after an install shipped, which the installer's
    /// onlyifdoesntexist flag would otherwise keep off that machine for good.
    ///
    /// <para>Named rather than repeated so a future shipped entry is one edit here plus the counts
    /// that follow from it, and so the list stays an explicit pin instead of something the test
    /// derives from the file it is testing.</para>
    /// </summary>
    private static readonly string[] AddedToTheOldShape = { "sp_ineachdb.sql", "sp_PerfCheck.sql" };

    /// <summary>Four installed entries at 8ff4cac plus everything shipped since.</summary>
    private static int MergedOldShapeCount => 4 + AddedToTheOldShape.Length;

    /// <summary>Every entry the shipped file carries. An empty installed array gains all of them.</summary>
    private static int ShippedEntryCount => 6;

    private static string ShippedConfigFileBesideTheBinary()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var folder in new[] { "config", "Config" })
        {
            var candidate = Path.Combine(baseDir, folder, "script-configurations.json");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "The shipped script configurations must be beside the test assembly: " + baseDir);
    }

    // -- (c) the merge itself --------------------------------------------------------------------

    [Fact]
    public void An_old_shape_config_gains_the_missing_shipped_entry()
    {
        var merged = MergeOldShape();

        var entries = Parse(merged);
        entries.Should().HaveCount(MergedOldShapeCount,
            "the 8ff4cac file had four entries and two shipped entries are newer than it");

        var prerequisite = entries.Single(e => e.ScriptPath == "sp_ineachdb.sql");
        prerequisite.ExecutionOrder.Should().Be(0,
            "sp_ineachdb must sort ahead of sp_Blitz, which is ExecutionOrder 1, because "
            + "LoadScriptConfigurations orders by it and FullAudit runs that order sequentially");
        prerequisite.Enabled.Should().BeTrue();

        // sp_PerfCheck is the second such entry, and it carries two properties that did not exist
        // when the older file was written. A JSON reader gives an absent property its default, so
        // an entry that arrived without them would capture result set 0 (the two-column server
        // banner, not the findings) and would report a healthy instance as a No-Rows fault.
        var perfCheck = entries.Single(e => e.ScriptPath == "sp_PerfCheck.sql");
        perfCheck.Enabled.Should().BeTrue();
        perfCheck.ExecutionOrder.Should().Be(2);
        perfCheck.OutputResultSetIndex.Should().Be(1);
        perfCheck.EmptyResultIsNormal.Should().BeTrue();
        perfCheck.ExportToCsv.Should().BeTrue();
    }

    [Fact]
    public void Every_pre_existing_byte_survives_the_merge_untouched()
    {
        var merged = MergeOldShape();

        // The strongest form of "never overwrite operator edits": the merge is a text splice, so
        // every byte up to and including the last pre-existing entry is the operator's own file.
        // The only thing after that point is the whitespace before the closing bracket, which has
        // to gain a comma, so the claim stops exactly where the honesty does.
        var lastEntryEnd = OldShapeInstalledConfig.LastIndexOf('}') + 1;
        lastEntryEnd.Should().BeGreaterThan(0);

        merged.Substring(0, lastEntryEnd).Should().Be(OldShapeInstalledConfig.Substring(0, lastEntryEnd),
            "not one byte of any installed entry may move");

        // And nothing was lost off the end either: the same four entries are still there, in the
        // same order, with the new ones appended after them.
        Parse(merged).Select(e => e.ScriptPath).Should().Equal(
            new[] { "sp_Blitz.sql", "Check_BP_Servers.sql", "stpChecklist_Seguranca.sql",
                    "SQLDBA.ORG.sp_triage.sql" }.Concat(AddedToTheOldShape));
    }

    [Fact]
    public void An_operator_edited_value_survives_the_merge()
    {
        // The operator changed sp_Blitz's timeout in the installed file. Nothing about the merge
        // may notice or care.
        var edited = OldShapeInstalledConfig.Replace(
            "\"TimeoutSeconds\": 900", "\"TimeoutSeconds\": 4321", StringComparison.Ordinal);
        edited.Should().NotBe(OldShapeInstalledConfig, "the fixture edit must have landed");

        ScriptConfigurationMigrator.TryMerge(edited, Shipped(), out var merged, out var added)
            .Should().BeTrue();

        added.Should().Equal(AddedToTheOldShape);

        var entries = Parse(merged!);
        entries.Where(e => !AddedToTheOldShape.Contains(e.ScriptPath))
            .Should().OnlyContain(e => e.TimeoutSeconds == 4321,
                "every pre-existing entry keeps the operator's value");

        // And the shipped entry arrives with the SHIPPED value, not the operator's.
        entries.Single(e => e.ScriptPath == "sp_ineachdb.sql").TimeoutSeconds.Should().Be(900);
    }

    [Fact]
    public void A_hand_edited_camel_case_config_is_keyed_the_same_way()
    {
        // The reader is case-insensitive (DiagnosticScriptRunner deserialises with
        // PropertyNameCaseInsensitive), so a hand-edited camelCase file runs perfectly well. The
        // merge must recognise its keys too, or it would add a duplicate of every entry.
        //
        // NOT the script editor's doing: SaveScriptConfigurations sets
        // JsonNamingPolicy.CamelCase, but ScriptConfiguration carries explicit [JsonPropertyName]
        // attributes that override it, so what the editor writes is PascalCase. The first
        // assertion below is a text transform for that reason, not a serialiser round trip.
        var camel = OldShapeInstalledConfig
            .Replace("\"Id\":", "\"id\":", StringComparison.Ordinal)
            .Replace("\"ScriptPath\":", "\"scriptPath\":", StringComparison.Ordinal);
        camel.Should().Contain("\"scriptPath\":").And.NotContain("\"ScriptPath\":");

        ScriptConfigurationMigrator.TryMerge(camel, Shipped(), out var merged, out var added)
            .Should().BeTrue();

        added.Should().Equal(AddedToTheOldShape);
        Parse(merged!).Should().HaveCount(MergedOldShapeCount,
            "four camelCase entries were recognised as already present, not duplicated");
    }

    [Fact]
    public void The_script_editor_writes_PascalCase_despite_its_camel_case_policy()
    {
        // Pins the fact the test above depends on. If someone ever removes the [JsonPropertyName]
        // attributes from ScriptConfiguration, SaveScriptConfigurations starts writing camelCase
        // for real and every installed file changes shape on the next Save - which the merge would
        // survive, but which nobody would have decided.
        var written = JsonSerializer.Serialize(
            Parse(OldShapeInstalledConfig),
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        written.Should().Contain("\"ScriptPath\":", "the model's attributes override the policy")
            .And.NotContain("\"scriptPath\":");
    }

    [Fact]
    public void The_merge_is_idempotent()
    {
        var first = MergeOldShape();

        ScriptConfigurationMigrator.TryMerge(first, Shipped(), out var second, out var added)
            .Should().BeFalse("a config that already has every shipped entry needs no merge");
        second.Should().BeNull("nothing to write means nothing is written");
        added.Should().BeEmpty();
    }

    [Fact]
    public void A_config_that_is_already_current_is_not_merged()
    {
        ScriptConfigurationMigrator.TryMerge(Shipped(), Shipped(), out var merged, out var added)
            .Should().BeFalse();
        merged.Should().BeNull();
        added.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void The_installed_file_keeps_its_own_line_endings(string newline)
    {
        var installed = OldShapeInstalledConfig.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (newline == "\r\n") installed = installed.Replace("\n", "\r\n", StringComparison.Ordinal);

        ScriptConfigurationMigrator.TryMerge(installed, Shipped(), out var merged, out _)
            .Should().BeTrue();

        if (newline == "\r\n")
            merged!.Replace("\r\n", "", StringComparison.Ordinal).Should().NotContain("\n",
                "a CRLF file must not gain lone LFs from the appended entry");
        else
            merged!.Should().NotContain("\r", "an LF file must not gain CRs from the appended entry");
    }

    // -- (b) the file-level guard: announce, never replace ----------------------------------------

    [Fact]
    public void An_unreadable_config_is_left_exactly_as_it_is_and_the_problem_is_announced()
    {
        var path = Path.Combine(_dir, "script-configurations.json");
        const string garbage = "{ this is not json at all";
        File.WriteAllText(path, garbage);
        var before = File.ReadAllBytes(path);

        var logger = new CapturingLogger();
        var added = ScriptConfigurationMigrator.EnsureShippedEntries(path, logger);

        added.Should().BeEmpty();
        File.ReadAllBytes(path).Should().Equal(before,
            "the config-store write guard: an unreadable store is NOT replaced with defaults");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning,
            "an unreadable store is announced, not swallowed");
    }

    [Fact]
    public void A_config_that_is_a_json_object_rather_than_an_array_is_left_alone()
    {
        var path = Path.Combine(_dir, "script-configurations.json");
        File.WriteAllText(path, "{ \"scripts\": [] }");
        var before = File.ReadAllBytes(path);

        var logger = new CapturingLogger();
        ScriptConfigurationMigrator.EnsureShippedEntries(path, logger).Should().BeEmpty();

        File.ReadAllBytes(path).Should().Equal(before);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void A_missing_config_is_not_created()
    {
        var path = Path.Combine(_dir, "does-not-exist.json");

        ScriptConfigurationMigrator.EnsureShippedEntries(path, NullLogger.Instance).Should().BeEmpty();

        File.Exists(path).Should().BeFalse(
            "the installer owns first delivery; this only ever adds to a file that is already there");
    }

    [Fact]
    public void The_file_is_written_once_and_each_addition_is_logged_by_key()
    {
        var path = Path.Combine(_dir, "script-configurations.json");
        File.WriteAllText(path, OldShapeInstalledConfig, new UTF8Encoding(false));

        var logger = new CapturingLogger();
        var added = ScriptConfigurationMigrator.EnsureShippedEntries(path, logger);

        added.Should().Equal(AddedToTheOldShape);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information && e.Message.Contains("sp_ineachdb.sql"),
            "each addition is announced at Information with its key");

        Parse(File.ReadAllText(path)).Should().HaveCount(MergedOldShapeCount);

        // Second run: nothing added, and the file is not rewritten.
        var afterFirst = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);

        var again = ScriptConfigurationMigrator.EnsureShippedEntries(path, new CapturingLogger());

        again.Should().BeEmpty();
        File.ReadAllBytes(path).Should().Equal(afterFirst);
        File.GetLastWriteTimeUtc(path).Should().Be(stamp, "an idempotent run does not touch the file");
    }

    /// <summary>
    /// The same "operator edits survive" claim, but through the half that actually WRITES.
    ///
    /// <para>The pure-merge version of this test does not cover it: replacing the write with
    /// File.WriteAllText(configPath, shipped) - the shipped defaults straight over the operator's
    /// file, every edit gone - left all fifteen other tests green (measured 2026-08-23). A merge
    /// that is correct and a writer that discards it are two different things.</para>
    /// </summary>
    [Fact]
    public void An_operator_edited_file_on_disk_is_added_to_and_never_replaced()
    {
        var path = Path.Combine(_dir, "script-configurations.json");
        var edited = OldShapeInstalledConfig.Replace(
            "\"TimeoutSeconds\": 900", "\"TimeoutSeconds\": 4321", StringComparison.Ordinal);
        edited.Should().NotBe(OldShapeInstalledConfig, "the fixture edit must have landed");
        File.WriteAllText(path, edited, new UTF8Encoding(false));

        var added = ScriptConfigurationMigrator.EnsureShippedEntries(path, new CapturingLogger());
        added.Should().Equal(AddedToTheOldShape);

        var after = File.ReadAllText(path);

        var lastEntryEnd = edited.LastIndexOf('}') + 1;
        after.Substring(0, lastEntryEnd).Should().Be(edited.Substring(0, lastEntryEnd),
            "the operator's file is added to, not rewritten");

        var entries = Parse(after);
        entries.Should().HaveCount(MergedOldShapeCount);
        entries.Where(e => !AddedToTheOldShape.Contains(e.ScriptPath))
            .Should().OnlyContain(e => e.TimeoutSeconds == 4321,
                "every edit the operator made is still on disk afterwards");
        entries.Single(e => e.ScriptPath == "sp_ineachdb.sql").TimeoutSeconds.Should().Be(900,
            "and the new entry arrives with the shipped value");
    }

    // -- (e) verifier findings, 2026-08-23 -------------------------------------------------------

    /// <summary>
    /// THE BLOCKING DEFECT the verifier found. An empty installed array behind ANY leading
    /// whitespace made the merge emit "[," - invalid JSON. The consequence was not cosmetic: the
    /// loader's JsonSerializer.Deserialize throws, DiagnosticScriptRunner catches and returns an
    /// empty list, so the install runs ZERO diagnostic scripts; and on the next startup the
    /// migrator correctly declines to touch an unparseable store, so the file it broke could never
    /// be repaired by the thing that broke it. The old guard asked whether the buffer held more
    /// than one character, which only answers "is anything already there" when the '[' is byte 0.
    ///
    /// <para>Every shape below is a JSON array holding nothing, so under the ruling every shipped
    /// entry is added to each - including the "an empty array cannot be a curated choice" case the
    /// class doc calls out.</para>
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData(" []")]
    [InlineData("\r\n[]")]
    [InlineData("\n[\n]\n")]
    [InlineData("  [  ]  ")]
    [InlineData("\t[]\t")]
    public void An_empty_array_merges_to_valid_json_whatever_whitespace_surrounds_it(string installed)
    {
        ScriptConfigurationMigrator.TryMerge(installed, Shipped(), out var merged, out var added)
            .Should().BeTrue("an empty array cannot be a curated choice, so it is migrated");

        added.Should().HaveCount(ShippedEntryCount);

        // The load-bearing assertion: the loader must be able to READ what the migrator wrote.
        // Parse() would hide a failure behind its ?? new List<>(), so deserialise directly here.
        var reread = JsonSerializer.Deserialize<List<ScriptConfiguration>>(
            merged!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        reread.Should().NotBeNull();
        reread!.Should().HaveCount(ShippedEntryCount,
            "a config-store writer must never emit a store it cannot read back");
        merged!.Should().NotContain("[,", "a leading comma is the exact shape of the defect");
    }

    [Fact]
    public void An_empty_config_on_disk_is_filled_and_the_loader_can_read_it_back()
    {
        // The same defect end to end, through the half that actually writes.
        var path = Path.Combine(_dir, "script-configurations.json");
        File.WriteAllText(path, "\r\n[]\r\n", new UTF8Encoding(false));

        var logger = new CapturingLogger();
        var added = ScriptConfigurationMigrator.EnsureShippedEntries(path, logger);

        added.Should().HaveCount(ShippedEntryCount);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);

        var reread = JsonSerializer.Deserialize<List<ScriptConfiguration>>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        reread.Should().NotBeNull();
        reread!.Should().HaveCount(ShippedEntryCount,
            "what was written back has to survive the loader's own deserialise");
    }

    /// <summary>
    /// The class doc promises every pre-existing byte survives. A UTF-8 BOM is a pre-existing byte
    /// and it did not survive: File.ReadAllText strips one and UTF8Encoding(false) does not put it
    /// back. No reader broke, but the claim was false, so the claim is now enforced.
    /// </summary>
    [Fact]
    public void A_byte_order_mark_the_operator_had_is_still_there_afterwards()
    {
        var path = Path.Combine(_dir, "script-configurations.json");
        File.WriteAllText(path, OldShapeInstalledConfig, new UTF8Encoding(true));
        File.ReadAllBytes(path).Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF },
            "the fixture must really carry a BOM or this test proves nothing");

        ScriptConfigurationMigrator.EnsureShippedEntries(path, NullLogger.Instance)
            .Should().Equal(AddedToTheOldShape);

        File.ReadAllBytes(path).Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF },
            "the file's own byte-order mark is one of the bytes the merge promises not to change");
        Parse(File.ReadAllText(path)).Should().HaveCount(MergedOldShapeCount);
    }

    [Fact]
    public void A_file_with_no_byte_order_mark_does_not_acquire_one()
    {
        // The other direction of the same fix: detection must not invent a preamble.
        var path = Path.Combine(_dir, "script-configurations.json");
        File.WriteAllText(path, OldShapeInstalledConfig, new UTF8Encoding(false));

        ScriptConfigurationMigrator.EnsureShippedEntries(path, NullLogger.Instance)
            .Should().Equal(AddedToTheOldShape);

        File.ReadAllBytes(path)[0].Should().Be((byte)'[',
            "a BOM-less file stays BOM-less; the shipped file has no mark and must not gain one");
    }

    /// <summary>
    /// The have-sets are now grown as entries are spliced, so a shipped file that names one script
    /// twice adds it once. The trigger is a repo-authoring mistake rather than anything an operator
    /// can do, but the result was a duplicate entry that would install and run the script twice.
    /// </summary>
    [Fact]
    public void A_shipped_file_that_names_one_script_twice_adds_it_once()
    {
        var doubled = JsonNode.Parse(Shipped())!.AsArray();
        var clones = doubled.Select(n => JsonNode.Parse(n!.ToJsonString())!).ToList();
        foreach (var clone in clones) doubled.Add(clone);
        doubled.Count.Should().Be(12, "the fixture must really hold each shipped entry twice");

        ScriptConfigurationMigrator.TryMerge(
                OldShapeInstalledConfig, doubled.ToJsonString(), out var merged, out var added)
            .Should().BeTrue();

        added.Should().Equal(AddedToTheOldShape);
        Parse(merged!).Select(e => e.ScriptPath).Should().OnlyHaveUniqueItems(
            "two entries pointing at one file would install and run it twice");
    }

    [Fact]
    public void The_shipped_file_itself_has_no_duplicate_keys()
    {
        // The guard above makes a duplicate harmless; this makes it visible. Nothing in the repo
        // checked that Config\script-configurations.json names each script and each Id once.
        var shipped = Parse(Shipped());

        shipped.Select(e => e.ScriptPath).Should().OnlyHaveUniqueItems();
        shipped.Select(e => e.Id).Should().OnlyHaveUniqueItems();
    }

    // -- the WIRING: the loader really runs the migration ----------------------------------------

    /// <summary>
    /// Drives the real DiagnosticScriptRunner.LoadScriptConfigurations against the real installed
    /// config beside the test binary, with the sp_ineachdb entry taken back out first, and puts
    /// the file back byte for byte afterwards.
    ///
    /// <para>WHY BOTHER. The 2026-08-22 fence lane learned this the hard way: decision functions
    /// can be perfect and fully tested while the call site that is supposed to use them is
    /// bypassed, and the whole suite stays green. Deleting the EnsureShippedEntries line from the
    /// loader must turn something red, and this is the something.</para>
    ///
    /// <para>It mutates a file shared by the test run, so it restores the exact bytes in a finally.
    /// An earlier version of this comment said the only other reader of that path was
    /// FrkLiveSmokeTests, which is inert unless FRK_LIVE_TARGET is set. That was WRONG, and the
    /// verifier caught it: FrkContractTests.ScriptConfigurations() reads the same file, and
    /// sp_ineachdb_is_installed_before_sp_Blitz_and_installs_only asserts the very entry this test
    /// removes. What actually makes it safe is that Tests/SQLTriage.Tests/xunit.runner.json sets
    /// parallelizeTestCollections false and maxParallelThreads 1, so no other class runs while
    /// this one holds the file. If parallelism is ever turned on, these two race and this test has
    /// to move to a private copy of the config instead.</para>
    /// </summary>
    [Fact]
    public void LoadScriptConfigurations_adds_a_missing_shipped_entry_before_it_reads()
    {
        var path = ShippedConfigFileBesideTheBinary();
        var original = File.ReadAllBytes(path);

        try
        {
            var withoutPrerequisite = RemoveEntry(Encoding.UTF8.GetString(original), "sp_ineachdb.sql");
            Parse(withoutPrerequisite).Should().HaveCount(ShippedEntryCount - 1,
                "the fixture must really be missing it");
            File.WriteAllText(path, withoutPrerequisite, new UTF8Encoding(false));

            var runner = new DiagnosticScriptRunner(
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                NullLogger<DiagnosticScriptRunner>.Instance);

            var configs = runner.LoadScriptConfigurations();

            var prerequisite = configs.SingleOrDefault(c => c.ScriptPath == "sp_ineachdb.sql");
            prerequisite.Should().NotBeNull(
                "the loader must add the shipped entry an upgraded install never received");

            var blitz = configs.Single(c => c.ScriptPath == "sp_Blitz.sql");
            prerequisite!.ExecutionOrder.Should().BeLessThan(blitz.ExecutionOrder,
                "sp_Blitz 8.34 hard-depends on sp_ineachdb, so it has to be installed first");
        }
        finally
        {
            File.WriteAllBytes(path, original);
        }
    }

    /// <summary>
    /// The sp_PerfCheck entry specifically, through the same route, because it is the first shipped
    /// entry whose behaviour depends on properties the older model did not have.
    ///
    /// <para>An install that predates the bundle has a config with no sp_PerfCheck at all. What it
    /// must gain is not just "an entry" but that entry's OutputResultSetIndex and
    /// EmptyResultIsNormal: without the first the run would export sp_PerfCheck's two-column server
    /// banner as if it were the findings, and without the second every healthy instance would be
    /// reported to the operator as a No-Rows fault. Both default to the wrong value if the property
    /// is missing, which is exactly what a splice that dropped unknown properties would produce.</para>
    /// </summary>
    [Fact]
    public void An_install_without_sp_PerfCheck_gains_exactly_that_entry()
    {
        var withoutPerfCheck = RemoveEntry(Shipped(), "sp_PerfCheck.sql");
        Parse(withoutPerfCheck).Should().HaveCount(ShippedEntryCount - 1,
            "the fixture must really be missing it");
        Parse(withoutPerfCheck).Should().NotContain(e => e.ScriptPath == "sp_PerfCheck.sql");

        ScriptConfigurationMigrator.TryMerge(withoutPerfCheck, Shipped(), out var merged, out var added)
            .Should().BeTrue();

        added.Should().Equal("sp_PerfCheck.sql");

        var entries = Parse(merged!);
        entries.Should().HaveCount(ShippedEntryCount);

        var perfCheck = entries.Single(e => e.ScriptPath == "sp_PerfCheck.sql");
        perfCheck.Name.Should().Be("sp_PerfCheck");
        perfCheck.Enabled.Should().BeTrue();
        perfCheck.ExecutionOrder.Should().Be(2);
        perfCheck.ExecutionTest.Should().BeEmpty();
        perfCheck.ExecutionParameters.Should().BeEmpty();
        perfCheck.SqlQueryForOutput.Should().Be("EXEC dbo.sp_PerfCheck;");
        perfCheck.ExportToCsv.Should().BeTrue();
        perfCheck.OutputResultSetIndex.Should().Be(1,
            "set 0 is the two-column server banner; the findings are set 1");
        perfCheck.EmptyResultIsNormal.Should().BeTrue(
            "an instance with no performance findings is healthy, not a broken export");

        // Nothing else moved.
        entries.Where(e => e.ScriptPath != "sp_PerfCheck.sql").Select(e => e.ScriptPath)
            .Should().Equal(Parse(withoutPerfCheck).Select(e => e.ScriptPath));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static string MergeOldShape()
    {
        ScriptConfigurationMigrator.TryMerge(OldShapeInstalledConfig, Shipped(), out var merged, out var added)
            .Should().BeTrue("the 8ff4cac config has no sp_ineachdb entry and the shipped one does");
        added.Should().Equal(AddedToTheOldShape);
        return merged!;
    }

    private static List<ScriptConfiguration> Parse(string json)
        => JsonSerializer.Deserialize<List<ScriptConfiguration>>(
               json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? new List<ScriptConfiguration>();

    /// <summary>Rebuilds a config text without one entry, through the model, so the fixture for the
    /// wiring test is an honest "older file" rather than a text hack.</summary>
    private static string RemoveEntry(string json, string scriptPath)
    {
        var kept = Parse(json).Where(e => e.ScriptPath != scriptPath).ToList();
        return JsonSerializer.Serialize(kept, new JsonSerializerOptions { WriteIndented = true });
    }
}
