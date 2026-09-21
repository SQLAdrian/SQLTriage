/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The three bypass surfaces the exec-surface-safety lane closed, tested per site.
    ///
    /// <para>Evidence classes are stated per test, honestly. The guard's safety DECISION is proved
    /// by execution in <see cref="DangerousExecGuardTests"/>. Here: the SAVE path and the dashboard
    /// LOAD path are proved by execution (a blocked query is refused / left out of the cache on a
    /// live call). The RUN path and the /query path are pinned by SOURCE STRUCTURE — the guard is
    /// invoked and positioned before the server is ever touched — because a full engine/Blazor
    /// execution harness is not available in this suite (there is no bUnit and the engine's live run
    /// needs a server connection). The structural pins guard against exactly the two regressions that
    /// matter: the guard being removed, or being moved AFTER execution.</para>
    /// </summary>
    public class ExecSurfaceGuardTests
    {
        private const string BlockedQuery = "EXEC xp_cmdshell 'whoami';";
        private const string SafeQuery = "SELECT * FROM sys.databases;";

        // ── Site 2 (save path): a blocked SqlQuery task is REFUSED at save — PROVED by execution ──

        [Fact]
        public void SavePath_BlockedSqlQueryTask_IsRefusedAndNotPersisted()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Dir, "scheduled-tasks.json");
            var svc = new ScheduledTaskDefinitionService(NullLogger<ScheduledTaskDefinitionService>.Instance, path);

            var result = svc.AddTask(new ScheduledTaskDefinition { Name = "evil", Query = BlockedQuery });

            Assert.False(result.Saved);
            Assert.NotNull(result.BlockedReason);
            Assert.Contains("xp_cmdshell", result.BlockedReason);
            Assert.Empty(svc.GetAllTasks());                 // not held in memory
            Assert.False(File.Exists(path) && File.ReadAllText(path).Contains("evil")); // not on disk
        }

        [Fact]
        public void SavePath_SafeSqlQueryTask_IsSaved()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Dir, "scheduled-tasks.json");
            var svc = new ScheduledTaskDefinitionService(NullLogger<ScheduledTaskDefinitionService>.Instance, path);

            var result = svc.AddTask(new ScheduledTaskDefinition { Name = "nightly", Query = SafeQuery });

            Assert.True(result.Saved);
            Assert.Equal("nightly", Assert.Single(svc.GetAllTasks()).Name);
        }

        [Fact]
        public void SavePath_UpdateWithBlockedQuery_IsRefused()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Dir, "scheduled-tasks.json");
            var svc = new ScheduledTaskDefinitionService(NullLogger<ScheduledTaskDefinitionService>.Instance, path);

            var ok = svc.AddTask(new ScheduledTaskDefinition { Name = "nightly", Query = SafeQuery });
            Assert.True(ok.Saved);
            var id = svc.GetAllTasks().Single().Id;

            var blocked = svc.UpdateTask(new ScheduledTaskDefinition { Id = id, Name = "nightly", Query = BlockedQuery });

            Assert.False(blocked.Saved);
            Assert.NotNull(blocked.BlockedReason);
            Assert.Contains("xp_cmdshell", blocked.BlockedReason);
            // The stored task still carries the SAFE query — the blocked edit did not land.
            Assert.Equal(SafeQuery, svc.GetTask(id)!.Query);
        }

        [Fact]
        public void SavePath_NonSqlQueryTask_IsNotGated_EvenWithDangerousText()
        {
            // The guard only gates SqlQuery; other task types ignore the Query field, so a dangerous
            // string parked there is not a scheduled exec and is not refused.
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Dir, "scheduled-tasks.json");
            var svc = new ScheduledTaskDefinitionService(NullLogger<ScheduledTaskDefinitionService>.Instance, path);

            var result = svc.AddTask(new ScheduledTaskDefinition
            {
                Name = "assessment",
                TaskType = TaskType.Assessment,
                Query = BlockedQuery
            });

            Assert.True(result.Saved);
            Assert.Equal("assessment", Assert.Single(svc.GetAllTasks()).Name);
        }

        // ── Site 1 half (dashboard LOAD path): a blocked panel is NOT cached — PROVED by execution ─

        [Fact]
        public void LoadPath_BlockedPanelAndSupportQuery_AreNotPlacedInTheExecutableCache()
        {
            var config = new DashboardConfigRoot();
            var dash = new DashboardDefinition { Id = "d1" };
            dash.Panels.Add(new PanelDefinition { Id = "safe.panel", PanelType = "DataGrid", Query = new QueryPair { SqlServer = SafeQuery } });
            dash.Panels.Add(new PanelDefinition { Id = "blocked.panel", PanelType = "DataGrid", Query = new QueryPair { SqlServer = BlockedQuery } });
            config.Dashboards.Add(dash);
            config.SupportQueries["safe.support"] = new QueryPair { SqlServer = "SELECT 1;" };
            config.SupportQueries["blocked.support"] = new QueryPair { SqlServer = BlockedQuery };

            var svc = new DashboardConfigService(NullLogger<DashboardConfigService>.Instance, config);

            Assert.True(svc.HasQuery("safe.panel"), "a safe panel must remain cached");
            Assert.False(svc.HasQuery("blocked.panel"), "a blocked panel must be left OUT of the cache (fail closed)");
            Assert.True(svc.HasQuery("safe.support"), "a safe support query must remain cached");
            Assert.False(svc.HasQuery("blocked.support"), "a blocked support query must be left OUT of the cache");
        }

        // ── Regression: the FULL shipped config must cache the same panel set as main ───────────────
        //    PROVED by execution. The 6072-suite exercised only DefaultConfigGenerator's ~42 panels, so
        //    the real 220+-query Config/dashboard-config.json never ran through the load-path gate. That
        //    gap let the wall silently drop two shipped panels — liveindexes.high_impact_missing (a
        //    CREATE INDEX recommendation the panel only DISPLAYS) and security.failed_logins_1h (an
        //    xp_instance_regread config read) — with no test to catch it. This loads the shipped file
        //    through the in-memory cache-rebuild seam and asserts the gate drops NOTHING: the cached set
        //    equals the full shipped set, which is what main cached before the lane.

        [Fact]
        public void LoadPath_FullShippedConfig_CachesEveryShippedQuery_NoneDroppedByTheGate()
        {
            var json = ReadRepoFile("Config/dashboard-config.json");
            var config = JsonSerializer.Deserialize<DashboardConfigRoot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            Assert.NotNull(config);

            // Every panel (flat + tab) with a non-empty id, plus every support-query key: the exact set
            // the cache would hold if the gate passed everything. RebuildQueryCache.IndexPanel skips only
            // empty ids (not a gate drop), so we mirror that skip and nothing else.
            var expected = new List<string>();
            foreach (var dashboard in config!.Dashboards)
            {
                foreach (var panel in dashboard.Panels)
                    if (!string.IsNullOrEmpty(panel.Id)) expected.Add(panel.Id);
                if (dashboard.Tabs != null)
                    foreach (var tab in dashboard.Tabs)
                        foreach (var panel in tab.Panels)
                            if (!string.IsNullOrEmpty(panel.Id)) expected.Add(panel.Id);
            }
            foreach (var key in config.SupportQueries.Keys)
                expected.Add(key);

            Assert.NotEmpty(expected); // the shipped file must actually carry panels

            var svc = new DashboardConfigService(NullLogger<DashboardConfigService>.Instance, config);

            var dropped = expected
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => !svc.HasQuery(id))
                .ToList();

            Assert.True(dropped.Count == 0,
                $"the load-path gate dropped {dropped.Count} shipped quer(ies) that main caches: {string.Join(", ", dropped)}");

            // Regression anchors named explicitly, so a future re-break points straight at the cause.
            Assert.True(svc.HasQuery("liveindexes.high_impact_missing"),
                "the CREATE INDEX display panel must cache (the guard blanks string literals and does not block CREATE INDEX)");
            Assert.True(svc.HasQuery("security.failed_logins_1h"),
                "the xp_instance_regread config panel must cache (a pure registry READ is allowed after the narrowing)");
        }

        // ── Site 1 run path + Site 3 (/query): the guard is wired in and gates execution ──────────
        //    Evidence class: SOURCE STRUCTURE (believe), not live execution. The decision these
        //    sites delegate to is proved by execution in DangerousExecGuardTests.

        [Fact]
        public void RunPath_InvokesTheGuardBeforeTouchingTheServer()
        {
            var src = ReadRepoFile("Data/Services/ScheduledTaskEngine.cs");

            var guardAt = src.IndexOf("DangerousExecGuard.Inspect(task.Query, ExecSurfacePolicy.Unattended)", StringComparison.Ordinal);
            var connAt = src.IndexOf("conn.GetConnectionString(serverName, task.Database)", StringComparison.Ordinal);
            var execAt = src.IndexOf("new SqlCommand(task.Query", StringComparison.Ordinal);

            Assert.True(guardAt >= 0, "the run path must invoke the guard under the UNATTENDED policy");
            Assert.True(connAt >= 0 && execAt >= 0, "expected the run path's connect + execute statements to exist");
            Assert.True(guardAt < connAt, "the guard must run BEFORE the connection string is built");
            Assert.True(guardAt < execAt, "the guard must run BEFORE the SqlCommand executes the query");
            // The guard is scoped to SqlQuery tasks and finalizes a block as Failed.
            Assert.Contains("task.TaskType == TaskType.SqlQuery", src);
        }

        [Fact]
        public void QueryExecutor_InvokesTheGuardBeforeExecuting()
        {
            var src = ReadRepoFile("Pages/QueryExecutor.razor");

            var guardAt = src.IndexOf("DangerousExecGuard.Inspect(SqlQuery)", StringComparison.Ordinal);
            var setCmdAt = src.IndexOf("cmd.CommandText = SqlQuery", StringComparison.Ordinal);
            var execAt = src.IndexOf("ExecuteReaderAsync()", StringComparison.Ordinal);

            Assert.True(guardAt >= 0, "/query must invoke DangerousExecGuard.Inspect(SqlQuery)");
            Assert.True(setCmdAt >= 0 && execAt >= 0, "expected /query's command build + execute to exist");
            Assert.True(guardAt < setCmdAt, "the guard must run BEFORE the command text is set");
            Assert.True(guardAt < execAt, "the guard must run BEFORE the reader executes");
        }

        // ── The destructive class at the surfaces (2026-09-07, lane exec-guard-ddl-class) ─────────

        private const string DestructiveQuery = "DROP TABLE dbo.Invoices;";
        private const string MaintenanceQuery = "ALTER INDEX ALL ON dbo.T REBUILD;";

        [Fact]
        public void SavePath_DestructiveSqlQueryTask_IsRefusedAndNotPersisted()
        {
            // PROVED BY EXECUTION, not by source: the save path really refuses and really does not
            // write. This is the surface whose gap was proved live on 2026-09-06.
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Dir, "scheduled-tasks.json");
            var svc = new ScheduledTaskDefinitionService(NullLogger<ScheduledTaskDefinitionService>.Instance, path);

            var result = svc.AddTask(new ScheduledTaskDefinition { Name = "dropper", Query = DestructiveQuery });

            Assert.False(result.Saved);
            Assert.NotNull(result.BlockedReason);
            Assert.Contains("DROP TABLE", result.BlockedReason);
            Assert.Contains("unattended surface", result.BlockedReason);
            Assert.Empty(svc.GetAllTasks());
            Assert.False(File.Exists(path) && File.ReadAllText(path).Contains("dropper"));
        }

        [Fact]
        public void SavePath_DynamicSqlTask_IsRefused()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Dir, "scheduled-tasks.json");
            var svc = new ScheduledTaskDefinitionService(NullLogger<ScheduledTaskDefinitionService>.Instance, path);

            var result = svc.AddTask(new ScheduledTaskDefinition
            {
                Name = "obfuscated",
                Query = "EXEC('DR' + 'OP TABLE dbo.Invoices');"
            });

            Assert.False(result.Saved);
            Assert.Contains("SQL built at run time", result.BlockedReason);
        }

        [Fact]
        public void SavePath_MaintenanceTask_IsStillSaved()
        {
            // The other direction, and the one that costs a customer if it regresses: the whole
            // point of a scheduled SqlQuery task is maintenance, and maintenance must still save.
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Dir, "scheduled-tasks.json");
            var svc = new ScheduledTaskDefinitionService(NullLogger<ScheduledTaskDefinitionService>.Instance, path);

            var result = svc.AddTask(new ScheduledTaskDefinition { Name = "reindex", Query = MaintenanceQuery });

            Assert.True(result.Saved, $"blocked as: {result.BlockedReason}");
            Assert.Equal("reindex", Assert.Single(svc.GetAllTasks()).Name);
        }

        [Fact]
        public void SavePath_NonSqlQueryTask_IsStillNotGated_ByTheNewClassEither()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Dir, "scheduled-tasks.json");
            var svc = new ScheduledTaskDefinitionService(NullLogger<ScheduledTaskDefinitionService>.Instance, path);

            var result = svc.AddTask(new ScheduledTaskDefinition
            {
                Name = "assessment",
                TaskType = TaskType.Assessment,
                Query = DestructiveQuery      // ignored by this task type; must not be gated
            });

            Assert.True(result.Saved, $"blocked as: {result.BlockedReason}");
        }

        [Fact]
        public void RunPath_ABlockLandsInTheExecRecord_TheToast_AndTheAuditLog()
        {
            // Evidence class: SOURCE STRUCTURE (believe), like the two ordering tests above — the
            // three sinks are what the ruling requires a block to reach on an unattended surface.
            var src = ReadRepoFile("Data/Services/ScheduledTaskEngine.cs");

            var guardAt = src.IndexOf("DangerousExecGuard.Inspect(task.Query, ExecSurfacePolicy.Unattended)", StringComparison.Ordinal);
            Assert.True(guardAt >= 0);
            var block = src.Substring(guardAt, Math.Min(2000, src.Length - guardAt));

            // (a) the task's exec record
            Assert.Contains("exec.Status = \"Failed\"", block);
            Assert.Contains("exec.ErrorMessage = $\"Blocked by dangerous-exec guard: {guard.Reason}\"", block);
            Assert.Contains("_history.UpdateExecution(exec)", block);
            // (b) the toast
            Assert.Contains("_toast.ShowError(task.Name", block);
            // (c) the audit log — NEW on this surface; before this lane the engine held no
            //     AuditLogService at all, so an unattended refusal left no filterable trace.
            Assert.Contains("_audit?.LogUnattendedExecBlocked(", block);
            Assert.Contains("AuditLogService.ExecSurfaces.ScheduledTaskRun", block);
            Assert.Contains("AuditLogService? audit = null", src);
        }

        [Fact]
        public void SavePath_ABlockAlsoReachesTheAuditLog()
        {
            var src = ReadRepoFile("Data/Services/ScheduledTaskDefinitionService.cs");

            Assert.Contains("DangerousExecGuard.Inspect(task.Query, ExecSurfacePolicy.Unattended)", src);
            Assert.Contains("_audit?.LogUnattendedExecBlocked(", src);
            Assert.Contains("AuditLogService.ExecSurfaces.ScheduledTaskSave", src);
        }

        [Fact]
        public void QueryExecutor_IsDeliberatelyNotOnTheUnattendedPolicy()
        {
            // Pins the SCOPING as a decision rather than an oversight. /query is attended: a person
            // typed the statement and is reading the result. Widening it would refuse a DBA's own
            // DROP on the console the product ships FOR ad-hoc statements. If someone later decides
            // otherwise, this test is where that argument has to be had.
            var src = ReadRepoFile("Pages/QueryExecutor.razor");

            Assert.Contains("DangerousExecGuard.Inspect(SqlQuery)", src);
            Assert.DoesNotContain("Inspect(SqlQuery, ExecSurfacePolicy", src);
        }

        [Fact]
        public void QueryExecutor_AppliesSessionSafetyAsARoundTrip_NotAsATextPrefix()
        {
            // F3: /query was the one query surface with no session safety. It must be applied as a
            // SET round-trip on the open connection — a text prefix would break any pasted batch
            // that must start with its own statement (CREATE PROCEDURE and friends).
            var src = ReadRepoFile("Pages/QueryExecutor.razor");

            var openAt = src.IndexOf("await conn.OpenAsync();", StringComparison.Ordinal);
            var applyAt = src.IndexOf("SqlSessionSafety.ApplyAsync(conn, readUncommitted: false)", StringComparison.Ordinal);
            var setCmdAt = src.IndexOf("cmd.CommandText = SqlQuery", StringComparison.Ordinal);

            Assert.True(applyAt >= 0, "/query must apply session safety on the connection");
            Assert.True(openAt >= 0 && setCmdAt >= 0);
            Assert.True(openAt < applyAt, "the options can only be set on an OPEN connection");
            Assert.True(applyAt < setCmdAt, "the options must be set BEFORE the user's batch is sent");

            // The user's batch is still sent verbatim — no prefix concatenation anywhere.
            Assert.Contains("cmd.CommandText = SqlQuery;", src);
            Assert.DoesNotContain("BuildPrefix", src);
            Assert.DoesNotContain("DefaultPrefix", src);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The REPO file of that name, anchored on the folder holding SQLTriage.sln.
        ///
        /// <para>⚠ THIS USED TO RETURN THE FIRST HIT WHILE WALKING UP FROM
        /// <c>AppContext.BaseDirectory</c>, AND THAT IS NOT THE REPO FILE.
        /// <c>bin\&lt;cfg&gt;\&lt;tfm&gt;\&lt;rid&gt;\</c> sits between the test assembly and the repo
        /// root, and it holds copies of the very files under guard.</para>
        ///
        /// <para>PROVED 2026-09-11, and not hypothetically — this is what made
        /// <see cref="LoadPath_FullShippedConfig_CachesEveryShippedQuery_NoneDroppedByTheGate"/> red on
        /// dev main. <c>DashboardConfigService</c>'s disk-backed constructor resolves
        /// <c>&lt;baseDir&gt;\Config\dashboard-config.json</c>, and its <c>Load()</c> calls
        /// <c>Save()</c> when that file is absent — so <c>CachedStatHonestyTests.cs:253</c> and
        /// <c>NoServerIdleTests.cs:116</c> write <c>DefaultConfigGenerator</c>'s ~42-panel default into
        /// the test output (100,274 bytes measured, where the shipped file is 481,763) and this helper
        /// then read THAT. <c>liveindexes.high_impact_missing</c> had not been dropped by the gate; it
        /// was never in the file. Before <c>3ba2a2b</c> (2026-09-10) the payload placed the real file in
        /// <c>config\</c>, so <c>File.Exists</c> was true, <c>Save()</c> never fired, and the collision
        /// was dormant — but the wrong-file READ has been happening for as long as this test has
        /// existed. Reproduced deliberately by planting a mutated copy in the output: the test failed
        /// naming the panel mutated THERE, with the repo file untouched.
        /// <c>ScheduledRunVerdictTests</c> carried the identical shape and was fixed with it;
        /// <see cref="ShippedConfigResolutionCensusTests.Every_ReadRepoFile_helper_anchors_on_the_repo_root"/>
        /// is what stops a ninth copy re-introducing it.</para>
        /// </summary>
        private static string ReadRepoFile(string relativePath)
        {
            var path = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"{relativePath} must exist at {path} to be asserted against. A missing file under "
                    + "guard is a failure, never a pass.", path);
            return File.ReadAllText(path);
        }

        private sealed class TempDir : IDisposable
        {
            public string Dir { get; }
            public TempDir()
            {
                Dir = Path.Combine(Path.GetTempPath(), "_sqlt_execsurface_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Dir);
            }
            public void Dispose()
            {
                try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
                catch { /* a locked file is not a test failure */ }
            }
        }
    }
}
