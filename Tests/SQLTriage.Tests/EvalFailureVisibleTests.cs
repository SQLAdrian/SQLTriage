/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// eval-failure-visible (fresh-eyes Lane D, ruling 2026-09-06 22:30): an alert that cannot be
    /// evaluated must be DURABLE (survive a restart), AUDITED once per episode + once on recovery (never
    /// per tick), and NEVER dispatched (no email/Teams/PagerDuty/toast — a monitoring gap is made
    /// visible, not paged). This file exercises each of those four properties directly.
    /// </summary>
    public class EvalFailureVisibleTests : IDisposable
    {
        private readonly string _tempDir;
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        public EvalFailureVisibleTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "eval-failure-visible-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        private string NewDir(string tag)
        {
            var d = Path.Combine(_tempDir, tag);
            Directory.CreateDirectory(d);
            return d;
        }

        // ── 1: persistence across a restart ─────────────────────────────────────────

        [Fact]
        public async Task AnEvaluationFailure_survivesARestart_reloadedIntoAFreshInstance()
        {
            var storePath = Path.Combine(NewDir("store-restart"), "eval-failures.json");
            var (connection, deadEndpoint) = DeadEndpoint();
            var alert = new AlertDefinition
            {
                Id = "io_err",
                Name = "SQL I/O Error",
                QueryMode = "io_error_check",
                Operator = "greater_than",
                Thresholds = new AlertThresholds { Warning = 1 },
            };

            // First process: force a failure through the real special-alert path and prove it recorded.
            using (var svc1 = BuildEngine(storePath: storePath))
            {
                await svc1.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());
                Assert.True(svc1.HasEvaluationFailure(deadEndpoint),
                    "a special handler that could not reach the server must record an evaluation failure");
            }

            Assert.True(File.Exists(storePath), "the failure store must be written to disk so it can survive a restart");

            // Second process: a fresh service over the SAME store reloads the failure. Pre-fix,
            // _evalFailures was in-memory only, so this instance read a false all-clear on restart.
            using (var svc2 = BuildEngine(storePath: storePath))
            {
                Assert.True(svc2.HasEvaluationFailure(deadEndpoint),
                    "a restart must reload the persisted evaluation failure, not silently read as all-clear");
                var f = Assert.Single(svc2.EvaluationFailures);
                Assert.Equal("io_err", f.AlertId);
                Assert.Equal(deadEndpoint, f.ServerName);
                Assert.NotEqual(default, f.FirstFailureUtc); // "failing since" survived the restart
            }
        }

        // ── 2: audit cardinality — one on open, one on recovery, never per tick ──────

        [Fact]
        public void AnEpisode_auditsExactlyOnceOnOpen_andOnceOnRecovery_neverPerTick()
        {
            var auditDir = NewDir("audit-card");
            var storePath = Path.Combine(NewDir("store-card"), "eval-failures.json");

            using var audit = new AuditLogService(auditDir, startFlushTimer: false);
            using (var svc = BuildEngine(storePath: storePath, audit: audit))
            {
                const string key = "alertx:srv1";
                // Three failing ticks in one episode. Only the FIRST may audit.
                svc.RecordEvaluationFailure(key, "alertx", "srv1", "unreachable");
                svc.RecordEvaluationFailure(key, "alertx", "srv1", "unreachable");
                svc.RecordEvaluationFailure(key, "alertx", "srv1", "still unreachable");
                // Recovery. Exactly one recovery entry.
                svc.ClearEvaluationFailure(key);
                // A second clear with no open episode must be a silent no-op — no phantom recovery.
                svc.ClearEvaluationFailure(key);
            }
            audit.Flush();

            var mine = ReadAll(auditDir)
                .Where(e => e.Details.TryGetValue("Category", out var c)
                            && c == AlertEvaluationService.EvalFailureAuditCategory
                            && e.Details.TryGetValue("AlertId", out var a) && a == "alertx")
                .ToList();

            var opened = mine.Where(e => e.Details["Phase"] == "Opened").ToList();
            var recovered = mine.Where(e => e.Details["Phase"] == "Recovered").ToList();

            Assert.Single(opened);
            Assert.Single(recovered);
            // The count travelled per tick even though the audit did not: three failing checks.
            Assert.Equal("3", recovered[0].Details["FailureCount"]);
            Assert.Equal(AuditSeverity.Warning, opened[0].Severity);
            Assert.Equal(AuditSeverity.Info, recovered[0].Severity);
        }

        [Fact]
        public void WithNoAuditSink_recordingAndClearingStillWork_andNeverThrow()
        {
            var storePath = Path.Combine(NewDir("store-noaudit"), "eval-failures.json");
            using var svc = BuildEngine(storePath: storePath, audit: null);

            const string key = "alerty:srv2";
            svc.RecordEvaluationFailure(key, "alerty", "srv2", "unreachable");
            Assert.True(svc.HasEvaluationFailure("srv2"));
            svc.ClearEvaluationFailure(key);
            Assert.False(svc.HasEvaluationFailure("srv2"));
        }

        // ── 3: never dispatches — behavioural (no toast is raised on the failure path) ─

        [Fact]
        public async Task ForcingEvaluationFailures_raisesNoToast_soNothingIsDispatched()
        {
            var toast = new ToastService();
            int shown = 0;
            toast.OnShow += _ => Interlocked.Increment(ref shown);

            var (connection, deadEndpoint) = DeadEndpoint();
            var storePath = Path.Combine(NewDir("store-nodispatch"), "eval-failures.json");
            using var svc = BuildEngine(storePath: storePath, toast: toast);

            // Both special-path failure shapes: a handler that returns null (io_error_check) and a
            // queryMode with no handler at all. DispatchNotification and DispatchEscalation BOTH raise a
            // toast, so any dispatch on the failure path would tick `shown`.
            await svc.EvaluateSpecialAlertAsync(
                new AlertDefinition { Id = "io_err", Name = "IO", QueryMode = "io_error_check", Operator = "greater_than", Thresholds = new AlertThresholds { Warning = 1 } },
                connection, deadEndpoint, new AlertGlobalDefaults());
            await svc.EvaluateSpecialAlertAsync(
                new AlertDefinition { Id = "mystery", Name = "M", QueryMode = "an_unwired_query_mode", Operator = "greater_than", Thresholds = new AlertThresholds { Warning = 1 } },
                connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.True(svc.HasEvaluationFailure(deadEndpoint), "the failures must actually have been recorded");
            Assert.Equal(0, shown); // zero toasts raised on the failure path = nothing dispatched
        }

        // ── 4: never dispatches — structural (the sink methods carry no Dispatch call) ─

        [Fact]
        public void TheEvaluationFailureSink_carriesNoDispatchCall_pinnedByStructure()
        {
            var root = RawPassedScan.RepoRoot().FullName;
            var src = File.ReadAllText(Path.Combine(root, "Data", "Services", "AlertEvaluationService.cs"));

            // `internal bool` since logon-failure-unmeasurable (2026-09-09): the sink returns whether
            // this call OPENED the episode, so the failure LOG can match the audit's once-per-episode
            // cardinality instead of inventing a second notion of "first time". The signature is spelled
            // out here on purpose — if it changes again, this lint says so rather than silently
            // scanning nothing.
            var record = ExtractMethodBody(src, "internal bool RecordEvaluationFailure(string stateKey");
            var clear = ExtractMethodBody(src, "internal void ClearEvaluationFailure(string stateKey");

            // These two are the ONLY sinks: every one of the six evaluation-path write/remove sites
            // routes through them (asserted below), so scanning them scans the whole failure path.
            // The ruling: a later "helpful" change that pages on a failure lands here and reddens this.
            Assert.DoesNotContain("Dispatch", record, StringComparison.Ordinal);
            Assert.DoesNotContain("Dispatch", clear, StringComparison.Ordinal);

            // No raw failure write escapes the sink. `_evalFailures[` may appear only inside the
            // reload path; the six evaluation sites must call the helpers, never mutate directly.
            var recordCalls = CountOccurrences(src, "RecordEvaluationFailure(stateKey");
            var clearCalls = CountOccurrences(src, "ClearEvaluationFailure(stateKey");
            Assert.Equal(4, recordCalls); // :937 special-null, :1042 special-catch, :1414 sql-catch, :1426 generic-catch
            Assert.Equal(2, clearCalls);  // :951 special-success, :1383 standard-success
        }

        // ── helpers ──────────────────────────────────────────────────────────────────

        private static string ExtractMethodBody(string src, string signatureStart)
        {
            var i = src.IndexOf(signatureStart, StringComparison.Ordinal);
            Assert.True(i >= 0, $"could not find method signature: {signatureStart}");
            var open = src.IndexOf('{', i);
            Assert.True(open >= 0, "no method body brace found");
            int depth = 0;
            for (int p = open; p < src.Length; p++)
            {
                if (src[p] == '{') depth++;
                else if (src[p] == '}')
                {
                    depth--;
                    if (depth == 0) return src.Substring(open, p - open + 1);
                }
            }
            throw new Xunit.Sdk.XunitException("unbalanced braces reading body of " + signatureStart);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
            return count;
        }

        private static List<AuditLogEntry> ReadAll(string dir) =>
            Directory.GetFiles(dir, "audit-*.jsonl")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .SelectMany(File.ReadAllLines)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<AuditLogEntry>(l, Json)!)
                .ToList();

        private static AlertEvaluationService BuildEngine(
            string storePath, AuditLogService? audit = null, ToastService? toast = null)
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
            var cache = new liveQueriesCacheStore();

            return new AlertEvaluationService(
                NullLogger<AlertEvaluationService>.Instance,
                new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                toast ?? new ToastService(),
                channels,
                cache,
                new InlineOrchestrator(),
                audit: audit,
                evalFailureStorePath: storePath);
        }

        private static (ServerConnection, string) DeadEndpoint()
        {
            const string deadEndpoint = "127.0.0.1,1"; // nothing listens here — refused immediately
            var connection = new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = deadEndpoint,
                UseWindowsAuthentication = true,
                ConnectionTimeout = 2,
                IsEnabled = true,
            };
            return (connection, deadEndpoint);
        }

        /// <summary>Runs the work inline, exactly as the real orchestrator does on the happy path.</summary>
        private sealed class InlineOrchestrator : IQueryOrchestrator
        {
            public async Task<QueryResult> EnqueueAsync(
                QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default)
            {
                try
                {
                    await request.Work(cancellationToken);
                    return new QueryResult { QueryId = request.QueryId, Success = true };
                }
                catch (Exception ex)
                {
                    return new QueryResult { QueryId = request.QueryId, Success = false, Exception = ex };
                }
            }

            public Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorHealth());
            public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorMetrics());
            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
