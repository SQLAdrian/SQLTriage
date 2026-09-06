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

            var guardAt = src.IndexOf("DangerousExecGuard.Inspect(task.Query)", StringComparison.Ordinal);
            var connAt = src.IndexOf("conn.GetConnectionString(serverName, task.Database)", StringComparison.Ordinal);
            var execAt = src.IndexOf("new SqlCommand(task.Query", StringComparison.Ordinal);

            Assert.True(guardAt >= 0, "the run path must invoke DangerousExecGuard.Inspect(task.Query)");
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

        // ── helpers ───────────────────────────────────────────────────────────────────────────────

        private static string ReadRepoFile(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);
                dir = dir.Parent;
            }
            throw new FileNotFoundException($"Could not locate {relativePath} by walking up from {AppContext.BaseDirectory}");
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
