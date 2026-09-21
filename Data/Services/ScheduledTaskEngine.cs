/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;

namespace SQLTriage.Data.Services
{
    // BM:ScheduledTaskEngine.Class — evaluates and executes scheduled tasks on timer
    public class ScheduledTaskEngine : IDisposable
    {
        private readonly ILogger<ScheduledTaskEngine> _logger;
        private readonly ScheduledTaskDefinitionService _definitions;
        private readonly ScheduledTaskHistoryService _history;
        private readonly ServerConnectionManager _connections;
        private readonly AzureBlobExportService? _blobExport;
        private readonly NotificationChannelService? _notifications;
        private readonly ToastService _toast;
        private readonly IQueryOrchestrator _orchestrator;
        private readonly CheckExecutionService? _checkExecution;
        // MSP #5: optional — supplies report branding (company name), colour-blind palette, and the
        // draft-watermark preference for a TaskType.Report. Absent in minimal DI/test graphs → sane defaults.
        private readonly UserSettingsService? _userSettings;
        // Optional so minimal DI/test graphs still construct this engine; the DI singleton
        // (ServiceCollectionExtensions.cs, WindowsServiceHost.cs) always supplies it, so a real
        // unattended tick always journals its refusals.
        private readonly AuditLogService? _audit;
        private readonly System.Timers.Timer _timer;

        private readonly SemaphoreSlim _evaluationLock = new(1, 1);

        private readonly ConcurrentDictionary<string, DateTime> _lastExecutionTime = new(StringComparer.OrdinalIgnoreCase);
        private bool _isRunning;
        private readonly CancellationTokenSource _cts = new();

        public event Action? OnTaskCompleted;

        // Announces that an assessment run has finished — nothing more. Always compiled,
        // in every build profile, and deliberately neutral about who consumes it:
        // build-gated modules subscribe via their own fenced registration (see
        // ServiceCollectionExtensions); a build with no subscriber never observes it (the
        // null-conditional raise below is a no-op then).
        public event EventHandler<AssessmentRunCompletedEventArgs>? AssessmentRunCompleted;

        public bool IsRunning => _isRunning;

        public ScheduledTaskEngine(
            ILogger<ScheduledTaskEngine> logger,
            ScheduledTaskDefinitionService definitions,
            ScheduledTaskHistoryService history,
            ServerConnectionManager connections,
            ToastService toast,
            IQueryOrchestrator orchestrator,
            AzureBlobExportService? blobExport = null,
            NotificationChannelService? notifications = null,
            CheckExecutionService? checkExecution = null,
            UserSettingsService? userSettings = null,
            AuditLogService? audit = null)
        {
            _logger = logger;
            _definitions = definitions;
            _history = history;
            _connections = connections;
            _toast = toast;
            _orchestrator = orchestrator;
            _blobExport = blobExport;
            _notifications = notifications;
            _checkExecution = checkExecution;
            _userSettings = userSettings;
            _audit = audit;

            // Tick every 60 seconds, check which tasks are due
            _timer = new System.Timers.Timer(60_000);
            _timer.Elapsed += (_, _) =>
            {
                _ = Task.Run(async () =>
                {
                    try { await ExecuteAllDueAsync(_cts.Token); }
                    catch (OperationCanceledException) { /* graceful shutdown */ }
                    catch (Exception ex) { _logger.LogError(ex, "Scheduled task cycle failed"); }
                });
            };
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            RestoreLastExecutionTimes();
            _timer.Start();
            _logger.LogInformation("Scheduled task engine started (60s tick)");
        }

        public void Stop()
        {
            // The teardown itself is UNCONDITIONAL and must stay that way: Dispose() also calls Stop(),
            // and a failed/partial Start() could otherwise leave a live timer behind. Only the log line
            // is guarded — it was printing twice on every shutdown (Stop() then Dispose()→Stop()),
            // which reads in the log like two engine instances were running.
            bool wasRunning = _isRunning;
            _isRunning = false;
            _timer.Stop();
            _cts.Cancel();
            if (wasRunning)
                _logger.LogInformation("Scheduled task engine stopped");
        }

        /// <summary>Bootstrap last execution times from SQLite so we don't re-run tasks after restart.</summary>
        private void RestoreLastExecutionTimes()
        {
            foreach (var task in _definitions.GetEnabledTasks())
            {
                var lastExec = _history.GetLastExecution(task.Id);
                if (lastExec != null)
                    _lastExecutionTime[task.Id] = lastExec.StartedAt;
            }
        }

        public async Task ExecuteAllDueAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _evaluationLock.WaitAsync(0, cancellationToken)) return;

            try
            {
                var tasks = _definitions.GetEnabledTasks();
                if (tasks.Count == 0) return;

                var now = DateTime.Now;
                var dueTasks = new List<Task>();

                foreach (var task in tasks)
                {
                    if (IsDue(task, now))
                    {
                        dueTasks.Add(ThrottledExecuteAsync(task, cancellationToken));
                    }
                }

                if (dueTasks.Count > 0)
                    await Task.WhenAll(dueTasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled task evaluation cycle failed");
            }
            finally
            {
                _evaluationLock.Release();
            }
        }

        /// <summary>Manual "Run Now" from the UI.</summary>
        public async Task RunTaskNowAsync(string taskId)
        {
            var task = _definitions.GetTask(taskId);
            if (task == null) return;
            await ExecuteTaskAsync(task);
        }

        private bool IsDue(ScheduledTaskDefinition task, DateTime now)
        {
            _lastExecutionTime.TryGetValue(task.Id, out var lastRun);
            return IsDue(task, now, lastRun);
        }

        /// <summary>Pure cadence evaluation — is <paramref name="task"/> due at <paramref name="now"/>
        /// given its <paramref name="lastRun"/> (default = never run)? Task-type-agnostic: a
        /// TaskType.Report rides the exact same ScheduleType gate as every other task. Internal test
        /// seam (InternalsVisibleTo SQLTriage.Tests).</summary>
        internal static bool IsDue(ScheduledTaskDefinition task, DateTime now, DateTime lastRun)
        {
            switch (task.Schedule.Type)
            {
                case ScheduleType.CustomInterval:
                    var interval = task.Schedule.IntervalMinutes ?? 60;
                    return lastRun == default || (now - lastRun).TotalMinutes >= interval;

                case ScheduleType.Daily:
                    if (!TimeSpan.TryParse(task.Schedule.TimeOfDay, out var dailyTime)) return false;
                    return (lastRun == default || lastRun.Date < now.Date) && now.TimeOfDay >= dailyTime;

                case ScheduleType.Weekly:
                    if (!TimeSpan.TryParse(task.Schedule.TimeOfDay, out var weeklyTime)) return false;
                    return now.DayOfWeek == (task.Schedule.DayOfWeek ?? DayOfWeek.Sunday)
                        && (lastRun == default || lastRun.Date < now.Date)
                        && now.TimeOfDay >= weeklyTime;

                case ScheduleType.Monthly:
                    if (!TimeSpan.TryParse(task.Schedule.TimeOfDay, out var monthlyTime)) return false;
                    var targetDay = Math.Min(task.Schedule.DayOfMonth ?? 1, DateTime.DaysInMonth(now.Year, now.Month));
                    return now.Day == targetDay
                        && (lastRun == default || lastRun.Date < now.Date)
                        && now.TimeOfDay >= monthlyTime;

                default:
                    return false;
            }
        }

        private async Task ThrottledExecuteAsync(ScheduledTaskDefinition task, CancellationToken cancellationToken)
        {
            var result = await _orchestrator.EnqueueAsync(new QueryRequest
            {
                QueryId = $"task:{task.Id}",
                Work = async _ => await ExecuteTaskAsync(task),
                CancellationToken = cancellationToken
            }, QueryPriority.P2_ScheduledTask, cancellationToken);

            if (!result.Success)
                _logger.LogError(result.Exception, "Scheduled task execution failed for {TaskName}", task.Name);
        }

        private async Task ExecuteTaskAsync(ScheduledTaskDefinition task)
        {
            var serverConnections = _connections.GetEnabledConnections();
            if (serverConnections.Count == 0) return;

            // MSP #5: a Report task produces ONE estate-wide artifact from the latest posture, so it
            // runs once per cycle rather than looping per-server like SqlQuery/Assessment tasks.
            if (task.TaskType == TaskType.Report)
            {
                await ExecuteReportTaskAsync(task, serverConnections);
                _lastExecutionTime[task.Id] = DateTime.Now;
                OnTaskCompleted?.Invoke();
                return;
            }

            var resolution = ResolveTargets(task, serverConnections);
            LogUnownedPinnedServerWarning(task, resolution);
            LogCollapsedDuplicateWarnings(task, resolution);

            foreach (var target in resolution.Targets)
            {
                await ExecuteOnServerAsync(task, target.Connection, target.ServerName);
            }

            // Stamped ONCE per cycle, after the whole fan-out. Do not move this inside the loop: the
            // Daily/Weekly/Monthly gates are same-calendar-day guards keyed on it, and stamping per
            // target would re-arm the task for every server in a legitimate multi-server fan-out.
            _lastExecutionTime[task.Id] = DateTime.Now;
            OnTaskCompleted?.Invoke();
        }

        /// <summary>One (connection, server) pair a task will execute against. The connection is the
        /// one that OWNS the address — it supplies the credential settings used to reach it.</summary>
        internal readonly record struct TaskTarget(ServerConnection Connection, string ServerName);

        /// <summary>Outcome of <see cref="ResolveTargets"/>. <paramref name="UnownedPinnedServer"/> is
        /// non-null only when the task pinned a server name that no enabled connection declares, and a
        /// fallback connection was substituted so the task still runs. <paramref name="CollapsedDuplicates"/>
        /// is null or empty on the pinned branch; on the unpinned (estate-wide) branch it carries one entry
        /// per address that a later connection declared again under <see cref="IsSameServer"/> and that
        /// was therefore folded into the first connection's target instead of running twice.</summary>
        internal sealed record TargetResolution(
            IReadOnlyList<TaskTarget> Targets,
            string? UnownedPinnedServer,
            IReadOnlyList<CollapsedDuplicate>? CollapsedDuplicates = null);

        /// <summary>One estate-wide duplicate the unpinned branch of <see cref="ResolveTargets"/> folded
        /// away. <paramref name="KeptConnection"/> is the first enabled connection that declared
        /// <paramref name="ServerName"/> (its credentials are the ones the single surviving target uses);
        /// <paramref name="DroppedConnection"/> is the later connection whose declaration of the same
        /// server (exact or alias, per <see cref="IsSameServer"/>) was collapsed into it.</summary>
        internal sealed record CollapsedDuplicate(
            string ServerName,
            ServerConnection KeptConnection,
            ServerConnection DroppedConnection);

        /// <summary>
        /// Resolves the exact set of (connection, server) pairs a task must run against. Pure and
        /// task-type-agnostic; internal test seam (InternalsVisibleTo SQLTriage.Tests).
        ///
        /// <para><b>The defect this replaces (2026-08-01).</b> The previous loop iterated every enabled
        /// connection and, when the task pinned a <c>ServerName</c>, substituted that one name for the
        /// connection's own server list. The pinned server was therefore assessed once PER ENABLED
        /// CONNECTION — three enabled connections on this box meant three full assessments of "." at
        /// 02:01:51 / 02:02:42 / 02:03:50 and six blob PUTs instead of two. Wasted work is the least of
        /// it: runs 2..N reached the pinned host carrying a DIFFERENT connection's credential settings
        /// (see <see cref="ServerConnection.GetConnectionString(string,string)"/>), which on an estate
        /// with per-connection SQL logins is either an auth failure or one server's credentials
        /// presented to another host.</para>
        ///
        /// <para><b>A pinned name resolves to exactly ONE target</b> — the first enabled connection that
        /// declares it. Two connections declaring the same address is a duplicate configuration, not a
        /// reason to run twice.</para>
        ///
        /// <para><b>Never zero targets.</b> A strict owner match that found no owner would silently stop
        /// the task forever, and the portal would go stale with nothing in the log to explain it — worse
        /// than the over-run being fixed. When no enabled connection owns the pinned name the first
        /// enabled connection is substituted and the caller logs a warning.</para>
        /// </summary>
        internal static TargetResolution ResolveTargets(
            ScheduledTaskDefinition task,
            IReadOnlyList<ServerConnection> enabledConnections)
        {
            if (enabledConnections == null || enabledConnections.Count == 0)
                return new TargetResolution(Array.Empty<TaskTarget>(), null);

            // Unpinned: fan out across every enabled connection's own server list — a task with no
            // ServerName is an estate-wide task by definition, and that part is unchanged.
            //
            // 2026-08-20 ruling (Adrian, soft dedupe): the fan-out used to add every declared
            // address with no dedup at all, so two connections declaring the same server (exact
            // string, or an alias pair like "." and this machine's name) produced two full
            // assessments of one host. That is the SAME defect class the pinned branch above was
            // fixed for on 2026-08-01, so it gets the SAME fix: collapse duplicates through the
            // house alias policy (IsSameServer — never a naive string Distinct, which would miss
            // alias-form duplicates) and keep the first enabled connection's target, matching the
            // pinned branch's and the Report path's "first connection wins" semantics. A collapsed
            // duplicate is not silent: the caller logs a warning per collapse naming the server,
            // both connections, and which one's credentials the surviving target uses, because two
            // connections declaring one server with DIFFERENT credentials is the real risk a soft
            // dedupe introduces — see LogCollapsedDuplicateWarnings.
            if (string.IsNullOrWhiteSpace(task.ServerName))
            {
                var kept = new List<TaskTarget>();
                List<CollapsedDuplicate>? collapsed = null;

                foreach (var conn in enabledConnections)
                {
                    foreach (var serverName in conn.GetServerList())
                    {
                        var priorIndex = kept.FindIndex(t => IsSameServer(t.ServerName, serverName));
                        if (priorIndex >= 0)
                        {
                            // Named by the KEPT connection's declared form (the address actually
                            // executed against), not the dropped connection's own string — the same
                            // "owning connection's declared address wins" convention the pinned
                            // branch below uses, so an operator sees one canonical name even when
                            // the two connections declared different alias spellings of one host.
                            collapsed ??= new List<CollapsedDuplicate>();
                            collapsed.Add(new CollapsedDuplicate(
                                kept[priorIndex].ServerName, kept[priorIndex].Connection, conn));
                            continue;
                        }
                        kept.Add(new TaskTarget(conn, serverName));
                    }
                }
                return new TargetResolution(kept, null, collapsed);
            }

            var pinned = task.ServerName.Trim();

            foreach (var conn in enabledConnections)
            {
                foreach (var declared in conn.GetServerList())
                {
                    if (!IsSameServer(declared, pinned)) continue;

                    // Execute against the address the OWNING CONNECTION declares, not the pinned
                    // string. They differ only when the two are aliases of one another, and the
                    // declared form is the one that carries the connection's port/instance and that
                    // every other estate surface (health cards, assessments, result keys) uses.
                    return new TargetResolution(
                        new[] { new TaskTarget(conn, declared) }, null);
                }
            }

            return new TargetResolution(
                new[] { new TaskTarget(enabledConnections[0], pinned) },
                pinned);
        }

        /// <summary>
        /// Do a declared address and a pinned task server name denote the same instance?
        ///
        /// <para><b>Alias policy, decided deliberately.</b> Two tiers, both PURE:</para>
        /// <list type="number">
        /// <item>Exact match — trimmed, <see cref="StringComparison.OrdinalIgnoreCase"/>. This is the
        /// same predicate the rest of the codebase already uses to ask "does this connection own that
        /// server" (e.g. AgentJobControlService), so the scheduler now agrees with it.</item>
        /// <item>Local-host alias equivalence — <c>.</c>, <c>(local)</c>, <c>localhost</c>,
        /// <c>127.0.0.1</c>, <c>::1</c> and this machine's own name all denote this box, so
        /// <c>MSI\OLD2017</c> matches <c>.\OLD2017</c>. Instance and port stay significant: a pinned
        /// <c>SQL01</c> must NEVER match a declared <c>SQL01\INST</c>, which is a different instance.</item>
        /// </list>
        ///
        /// <para><b>Why not the B1 canonical resolver</b> (<c>ConnectionHealthService.ResolveCanonical</c>,
        /// which maps <c>.\OLD2017</c> → <c>MSI\OLD2017</c> from SERVERPROPERTY): it is a LIVE PROBE
        /// CACHE. It is empty until a health check has run, and permanently empty for a server that is
        /// unreachable. Resolving targets through it would make the set of servers a nightly task runs
        /// against depend on probe timing and server availability — the same class of nondeterminism as
        /// the defect above, and it would additionally couple the scheduler to health-probe state.
        /// Target resolution must be answerable from configuration alone.</para>
        ///
        /// <para>Tier 2 exists only to stop tier 3 (the fallback) firing a warning every single night on
        /// a configuration that is in fact correct — a warning an operator would learn to ignore.</para>
        /// </summary>
        internal static bool IsSameServer(string? declared, string? pinned)
        {
            var d = (declared ?? string.Empty).Trim();
            var p = (pinned ?? string.Empty).Trim();
            if (d.Length == 0 || p.Length == 0) return false;

            if (string.Equals(d, p, StringComparison.OrdinalIgnoreCase)) return true;

            return string.Equals(
                NormalizeLocalAlias(d), NormalizeLocalAlias(p), StringComparison.OrdinalIgnoreCase);
        }

        private static readonly string[] LocalHostAliases = { ".", "(local)", "localhost", "127.0.0.1", "::1" };

        private static bool IsLocalHostAlias(string host) =>
            Array.Exists(LocalHostAliases, a => string.Equals(a, host, StringComparison.OrdinalIgnoreCase))
            || string.Equals(host, System.Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Rewrites the HOST segment of an address to "." when it is one of this machine's
        /// aliases, leaving instance and port untouched. Address parsing is delegated to
        /// <see cref="ServerAddress.HostOnly"/> so "host\instance,port" is never mis-split.</summary>
        private static string NormalizeLocalAlias(string address)
        {
            var s = address.Trim();
            if (s.Length == 0) return string.Empty;
            if (IsLocalHostAlias(s)) return ".";                    // bare alias, no instance or port

            var host = ServerAddress.HostOnly(s);
            if (host.Length > 0
                && IsLocalHostAlias(host)
                && s.StartsWith(host, StringComparison.OrdinalIgnoreCase))
            {
                return "." + s.Substring(host.Length);
            }
            return s;
        }

        /// <summary>
        /// Logs the "pinned server not owned by any enabled connection, falling back" warning
        /// <see cref="ResolveTargets"/> can produce. Shared by every task type (4f: the Report path
        /// used to bypass <see cref="ResolveTargets"/> entirely and had no equivalent warning).
        /// </summary>
        private void LogUnownedPinnedServerWarning(ScheduledTaskDefinition task, TargetResolution resolution)
        {
            if (resolution.UnownedPinnedServer != null && resolution.Targets.Count > 0)
            {
                _logger.LogWarning(
                    "Scheduled task '{TaskName}' is pinned to server '{PinnedServer}', which no enabled " +
                    "connection declares. Falling back to connection '{Connection}' so the task still runs. " +
                    "Check the task's server name against the connection list.",
                    task.Name, resolution.UnownedPinnedServer, resolution.Targets[0].Connection.ServerNames);
            }
        }

        /// <summary>
        /// Logs one warning per <see cref="TargetResolution.CollapsedDuplicates"/> entry the unpinned
        /// branch of <see cref="ResolveTargets"/> can produce (2026-08-20 soft-dedupe ruling). The
        /// warning names the server, the kept and dropped declaration, and which connection's
        /// credentials the surviving target uses. Worded to stay true for BOTH shapes the dedupe
        /// collapses: two different connections declaring the same server, and one connection listing
        /// the same server twice (self-collision — <c>KeptConnection</c> and <c>DroppedConnection</c>
        /// are then the same object) — different credentials for one server is the real risk either
        /// shape can hide, so an operator reading the log must be able to see it without opening the
        /// connection list.
        /// </summary>
        private void LogCollapsedDuplicateWarnings(ScheduledTaskDefinition task, TargetResolution resolution)
        {
            if (resolution.CollapsedDuplicates == null) return;

            foreach (var dup in resolution.CollapsedDuplicates)
            {
                _logger.LogWarning(
                    "Scheduled task '{TaskName}': server '{ServerName}' was declared more than once. " +
                    "Running it once under '{KeptConnection}' ({KeptCredentials}); the duplicate " +
                    "declaration via '{DroppedConnection}' is not used. If the declarations carry " +
                    "different credentials for this server, check the connection list before relying " +
                    "on the collapsed run.",
                    task.Name, dup.ServerName, dup.KeptConnection.ServerNames,
                    DescribeCredentials(dup.KeptConnection), dup.DroppedConnection.ServerNames);
            }
        }

        /// <summary>Short, password-free description of the login a connection uses — enough for an
        /// operator reading <see cref="LogCollapsedDuplicateWarnings"/> to compare two connections'
        /// credentials without opening the connection list.</summary>
        private static string DescribeCredentials(ServerConnection conn) => conn.EffectiveAuthType switch
        {
            AuthenticationTypes.SqlServer => $"SQL Server Authentication as '{conn.Username}'",
            AuthenticationTypes.EntraMFA => string.IsNullOrWhiteSpace(conn.Username)
                ? "Microsoft Entra MFA"
                : $"Microsoft Entra MFA as '{conn.Username}'",
            _ => "Windows Authentication",
        };

        /// <summary>
        /// MSP #5 — renders a branded executive/estate report from the LATEST posture across the
        /// in-scope server(s), writes it to output\reports\, and (opt-in) emails it as a PDF attachment.
        /// Uses the PURE-QuestPDF ScheduledReportService path — never PrintService/CoreWebView2, which
        /// cannot run in this background context. Does NOT re-run checks: it reflects whatever the most
        /// recent assessment left in the result store, so it is truly independently scheduled. Best-effort:
        /// a failure is logged + recorded, never thrown, so one bad cycle doesn't stall the engine.
        /// </summary>
        private async Task ExecuteReportTaskAsync(
            ScheduledTaskDefinition task, IReadOnlyList<ServerConnection> serverConnections)
        {
            // 4f: this used to filter serverConnections with an inline exact-match on
            // task.ServerName, so an alias-pinned report (e.g. pinned ".\OLD2017" against a
            // connection that declares "MSI\OLD2017") matched nothing and silently rendered a
            // report over zero servers. ResolveTargets is the ONE alias policy (local-host
            // equivalence + the unowned-pin fallback) every other task type already runs through;
            // Report just needs the distinct server set off it, since it aggregates estate-wide
            // rather than running once per (connection, server) pair.
            var resolution = ResolveTargets(task, serverConnections);
            LogUnownedPinnedServerWarning(task, resolution);
            LogCollapsedDuplicateWarnings(task, resolution);

            var servers = resolution.Targets
                .Select(t => t.ServerName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Provisional: the target set, used only if this task fails before it knows what it
            // measured (the catch below labels its toast and its log line with this). It is
            // REPLACED by the measured set as soon as the results are in — see ScopeLabel.
            var scopeLabel = ScopeLabel(servers, servers.Count);

            // Zero-target guard. Distinct from the Report(0)-results Warning further down, which
            // means "servers resolved fine, nothing cached yet" — this means resolution itself
            // came back empty (only reachable when every enabled connection declares no servers;
            // a pinned task always gets >=1 target via the fallback above). §4.5 honesty rail:
            // never silently produce a "0 servers" report — record it, log it, toast it.
            if (servers.Count == 0)
            {
                var exec0 = new ScheduledTaskExecution
                {
                    TaskId = task.Id,
                    TaskName = task.Name,
                    ServerName = "All Servers",
                    Status = ScheduledRunVerdict.Warning,
                    ErrorMessage = "The report resolved to zero target servers. Check the task's "
                                  + "server pin (if any) and the enabled connection list.",
                    StartedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow,
                };
                exec0.Id = _history.InsertExecution(exec0);
                _history.UpdateExecution(exec0);

                _logger.LogWarning(
                    "Scheduled report '{TaskName}' resolved to zero target servers. No report was rendered.",
                    task.Name);
                _toast.ShowWarning("Scheduled report",
                    $"{task.Name}: resolved to zero target servers. Check the server pin.", 8000);
                return;
            }

            var exec = new ScheduledTaskExecution
            {
                TaskId = task.Id,
                TaskName = task.Name,
                ServerName = servers.Count == 1 ? servers[0] : "All Servers",
                Status = "Running",
                StartedAt = DateTime.UtcNow
            };
            exec.Id = _history.InsertExecution(exec);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Latest posture only — the report is a snapshot renderer, not a re-scan.
                //
                // reports-r1-09 (2026-08-27): the subtitle was stamped from the full resolved target
                // list while the findings came only from servers that actually produced rows, and
                // CheckExecutionService.GetResults returns an EMPTY list for an instance the licence
                // does not cover. So this unattended, unreviewed PDF named servers it never measured
                // and said nothing about the omission. Measured at the seam before the fix, with a
                // one-seat register over three targets: "Estate Report Executive Briefing 3 servers ·
                // SRV1, SRV2, SRV3  0% Passed  0 of 1 checks passed" — the words "excluded" and
                // "licence" appeared nowhere. GetResults' own contract says callers that list servers
                // must surface GetExcludedServers(); this caller is now one of them.
                var results = new List<CheckResult>();
                var contributed = new List<string>();
                var noResults = new List<string>();
                foreach (var s in servers)
                {
                    var rows = _checkExecution?.GetResults(s, maxCount: 2000) ?? new List<CheckResult>();
                    if (rows.Count > 0) { results.AddRange(rows); contributed.Add(s); }
                    else noResults.Add(s);
                }

                // The licence-excluded subset of THIS task's targets, named separately because
                // "not covered by your licence" and "nothing has been audited yet" are different
                // facts and a client reading a scope line deserves to know which one applies.
                var excludedAll = _checkExecution?.GetExcludedServers() ?? new List<string>();
                var licenceExcluded = servers
                    .Where(s => excludedAll.Contains(s, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var noResultsOnly = noResults
                    .Where(s => !licenceExcluded.Contains(s, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var coverageNote = BuildReportCoverageNote(servers.Count, contributed.Count, licenceExcluded, noResultsOnly);
                if (coverageNote.Length > 0)
                    _logger.LogWarning(
                        "Scheduled report '{TaskName}' covers {Contributed} of {Targets} target servers. {Note}",
                        task.Name, contributed.Count, servers.Count, coverageNote);

                var nowUtc = DateTime.UtcNow;
                var runId  = Guid.NewGuid().ToString("N")[..8];
                var tz     = TimeZoneInfo.Local.StandardName;

                var company = !string.IsNullOrWhiteSpace(task.Report.CompanyName)
                    ? task.Report.CompanyName
                    : (_userSettings?.GetReportCompanyName() ?? "");
                var cb = _userSettings?.GetColorBlindMode() ?? false;

                var envs = serverConnections
                    .Where(c => c.GetServerList().Any(n => servers.Contains(n, StringComparer.OrdinalIgnoreCase)))
                    .Select(c => c.Environment)
                    .ToList();
                var watermark = (_userSettings?.GetReportDraftWatermark() ?? false)
                    && ServerEnvironment.RequiresWatermark(envs);

                // The scope line names the servers whose data is IN the report. Targets that
                // contributed nothing are named in the coverage note instead, with the reason.
                //
                // reports-r1-09, fix round 2026-08-27: the first fix moved the COVER SUBTITLE onto
                // the contributed set and left everything else on the target count. One delivered
                // PDF therefore said "SRV1" on its cover, "3 servers" on every page footer, arrived
                // as ExecutiveBriefing_3_servers_….pdf, and was announced by a client email whose
                // subject read "SQLTriage Estate Report — 3 servers" with no coverage note in it at
                // all. Four labels, one measured estate. They all take ScopeLabel now.
                //
                // The nothing-contributed case is stated rather than falling back to the target
                // list: a report over zero measured servers is not a report about those servers,
                // and the fallback reinstated the exact subtitle this finding is about.
                scopeLabel = ScopeLabel(contributed, servers.Count);
                var meta = new AssessmentMeta
                {
                    Title        = "Estate Report",
                    Company      = company,
                    Subtitle     = ScopeSubtitle(contributed, servers.Count),
                    CoverageNote = coverageNote,
                    Engine       = "Corpus audit checks",
                    GeneratedUtc = nowUtc.ToString("yyyy-MM-ddTHH:mmZ"),
                    TimezoneId   = tz,
                    RunId        = runId,
                    ColorBlind   = cb,
                    Watermark    = watermark,
                    FooterMeta   = $"SQLTriage — Executive Briefing — {scopeLabel} — {nowUtc:yyyy-MM-ddTHH:mmZ} ({tz}) — Run {runId}",
                };

                var briefing = ScheduledReportService.BuildBriefing(meta, results);
                var pdf      = ScheduledReportService.Render(task.Report.Kind, briefing);
                var path     = ScheduledReportService.SaveToReportsFolder(pdf, scopeLabel, nowUtc, runId);

                sw.Stop();
                exec.RowCount        = briefing.Findings.Count;
                exec.DurationSeconds = sw.Elapsed.TotalSeconds;
                exec.CompletedAt     = DateTime.UtcNow;
                exec.CsvFilePath     = path;   // reuse the output-path column for the report file

                // Sibling of the assessment path's zero-check guard (2026-08-05). A report built
                // over ZERO cached check results is a rendered PDF of nothing, and it used to be
                // recorded "Success".
                (exec.Status, exec.ErrorMessage) = ScheduledRunVerdict.Report(results.Count);

                // Delivery: email the PDF as an attachment. Opt-in — only when the task carries
                // recipients and the email channel is wired. Row-authorized for MSP #5.
                var deliveryAttempted = false;
                var deliverySucceeded = false;
                var deliveryReason = "";
                if (task.Output.SendEmail && _notifications != null
                    && task.Report.Recipients.Any(r => !string.IsNullOrWhiteSpace(r)))
                {
                    deliveryAttempted = true;
                    var subject = $"SQLTriage Estate Report — {scopeLabel} — {nowUtc:yyyy-MM-dd}";
                    // The coverage note travels with the attachment. The recipient of an unattended
                    // report reads the email first and may never open the PDF; a subject naming a
                    // scope with no qualifier beside it is the same over-claim the cover carried.
                    var body =
                        $"<p>The scheduled SQLTriage executive report for {System.Net.WebUtility.HtmlEncode(scopeLabel)} is attached.</p>" +
                        (coverageNote.Length > 0
                            ? $"<p>{System.Net.WebUtility.HtmlEncode(coverageNote)}</p>"
                            : "") +
                        $"<p>Generated {nowUtc:yyyy-MM-dd HH:mm} UTC · Run {runId}.</p>";
                    var sent = await _notifications.SendReportEmailAsync(task.Report.Recipients, subject, body, path);
                    exec.EmailSent = sent.Success;
                    deliverySucceeded = sent.Success;
                    deliveryReason = sent.Message ?? "";
                    if (!sent.Success)
                        _logger.LogWarning("Scheduled report '{Name}': email not sent — {Reason}", task.Name, sent.Message);
                }

                _history.UpdateExecution(exec);

                // The toast is a claim too (2026-08-05). It said "report generated" over a FAILED
                // delivery, so an operator watching the screen saw a success while the recipients
                // got nothing. It now reports what each step measured, separately.
                var toastDetail = $"{scopeLabel}: report generated ({sw.Elapsed.TotalSeconds:F1}s)";
                if (results.Count == 0)
                    toastDetail += ", over zero check results";
                if (deliveryAttempted && !deliverySucceeded)
                {
                    _toast.ShowWarning("Scheduled report",
                        $"{toastDetail}. Email was NOT sent"
                        + (string.IsNullOrWhiteSpace(deliveryReason) ? "." : $": {deliveryReason}"), 8000);
                }
                else
                {
                    if (deliveryAttempted) toastDetail += ", emailed";
                    _toast.ShowSuccess("Scheduled report", toastDetail, 4000);
                }
                _logger.LogInformation(
                    "Scheduled report '{Name}' generated for {Scope}: {Findings} findings in {Duration:F1}s",
                    task.Name, LogAnon.S(scopeLabel), briefing.Findings.Count, sw.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                exec.Status = "Failed";
                // §4.5 honesty rail: fixed context + exception TYPE only — never ex.Message, which can
                // embed connection strings / hostnames on a SQL or transport fault.
                exec.ErrorMessage    = $"Report generation failed ({ex.GetType().Name}).";
                exec.DurationSeconds = sw.Elapsed.TotalSeconds;
                exec.CompletedAt     = DateTime.UtcNow;
                _history.UpdateExecution(exec);

                _toast.ShowError("Scheduled report", $"Failed for {scopeLabel}", 6000);
                _logger.LogWarning("Scheduled report '{Name}' failed for {Scope} ({ExType})",
                    task.Name, LogAnon.S(scopeLabel), ex.GetType().Name);
            }
        }

        /// <summary>
        /// The scheduled report's coverage sentence (reports-r1-09): what share of the task's target
        /// servers actually contributed, and which ones did not, with the reason. Empty when every
        /// target contributed, so an unaffected report renders exactly as it always has.
        ///
        /// <para>Rides <see cref="AssessmentMeta.CoverageNote"/>, which the Executive Briefing prints
        /// on the COVER beside the pass-rate donut and again on the summary page — the two things a
        /// reader takes away. An unattended report nobody reviews before it emails out is the last
        /// place a scope line may name servers it never measured.</para>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests): the full ExecuteReportTaskAsync chain
        /// needs the whole scheduler DI, so the sentence is measurable here on its own.</para>
        /// </summary>
        internal static string BuildReportCoverageNote(
            int targetCount, int contributedCount,
            IReadOnlyList<string> licenceExcluded, IReadOnlyList<string> noResults)
        {
            if (licenceExcluded.Count == 0 && noResults.Count == 0) return string.Empty;

            var note = $"Coverage: {contributedCount} of {targetCount} target server"
                     + (targetCount == 1 ? "" : "s") + " contributed data to this report.";
            if (licenceExcluded.Count > 0)
                note += $" Not covered by your licence, so nothing was assessed on them: {NameList(licenceExcluded)}.";
            if (noResults.Count > 0)
                note += $" No cached check results, so nothing was assessed on them: {NameList(noResults)}.";
            return note;
        }

        /// <summary>
        /// THE one scope label for a scheduled report (reports-r1-09, fix round 2026-08-27). Every
        /// place that names this report's estate takes it: the page footer on every page
        /// (AssessmentMeta.FooterMeta), the delivered file name
        /// (ScheduledReportService.SaveToReportsFolder) and the client email subject. They used to be
        /// stamped from the TARGET list while the cover was stamped from the measured one, so a
        /// single delivery described two different estates.
        ///
        /// <para><paramref name="measured"/> is the set that actually contributed rows. Zero measured
        /// servers is stated as a ratio, never as the target list: a report over nothing measured is
        /// not a report about the servers it aimed at, and naming them there is the over-claim this
        /// finding is about. The count is safe in a file name (SaveToReportsFolder sanitises).</para>
        /// </summary>
        internal static string ScopeLabel(IReadOnlyList<string> measured, int targetCount) =>
            measured.Count == 0 ? $"0 of {targetCount} servers measured"
          : measured.Count == 1 ? measured[0]
          : $"{measured.Count} servers";

        /// <summary>The cover subtitle for the same scope: <see cref="ScopeLabel"/> plus the names,
        /// which the cover has room for and a file name does not. The coverage note carries the
        /// servers that contributed nothing, with the reason, and prints directly beneath it.</summary>
        internal static string ScopeSubtitle(IReadOnlyList<string> measured, int targetCount) =>
            measured.Count == 0
                ? $"No data from any of the {targetCount} target server{(targetCount == 1 ? "" : "s")}"
                : measured.Count == 1
                    ? measured[0]
                    : $"{measured.Count} servers · {NameList(measured)}";

        /// <summary>Comma-joined names, capped so one large estate cannot run the line off the page.
        /// The remainder is COUNTED, never dropped silently.</summary>
        private static string NameList(IReadOnlyList<string> names, int max = 6)
            => names.Count <= max
                ? string.Join(", ", names)
                : string.Join(", ", names.Take(max)) + $" and {names.Count - max} more";

        private async Task ExecuteOnServerAsync(ScheduledTaskDefinition task, ServerConnection conn, string serverName)
        {
            var exec = new ScheduledTaskExecution
            {
                TaskId = task.Id,
                TaskName = task.Name,
                ServerName = serverName,
                Status = "Running",
                StartedAt = DateTime.UtcNow
            };
            exec.Id = _history.InsertExecution(exec);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // P3 retention loop: an Assessment task re-runs the full enabled-check suite,
            // which records check history and (on first run) freezes the baseline. This is
            // the recurring re-scan the baseline timeline depends on.
            if (task.TaskType == TaskType.Assessment)
            {
                await ExecuteAssessmentOnServerAsync(conn, serverName, exec, sw);
                return;
            }

            // MSP #9: a RestoreVerify task auto-generates per-DB RESTORE VERIFYONLY for the most recent
            // full backup and classifies each outcome (Passed / Failed-corrupt / CouldNotRun / NotSupported).
            if (task.TaskType == TaskType.RestoreVerify)
            {
                await ExecuteRestoreVerifyOnServerAsync(task, conn, serverName, exec, sw);
                return;
            }

            // exec-surface-safety: a free-form SqlQuery task's T-SQL is gated per-statement for the
            // OS/system-exec class (xp_cmdshell, OLE automation, registry, CLR loading, and the
            // sp_configure toggles that enable them) BEFORE it touches the server. The gate is
            // position-independent, so a leading sys.* read cannot shield a trailing dangerous
            // statement — with or without a semicolon between them (proved by test after fix round 2,
            // 2026-09-07; before that fix two of the destructive rules were anchored at '^' and a
            // semicolon-free lead DID shield them).
            // Maintenance DDL/DML passes. Only SqlQuery runs free-form text here; the other task
            // types above ignore the Query field and are untouched.
            if (task.TaskType == TaskType.SqlQuery)
            {
                // ExecSurfacePolicy.Unattended (2026-09-07): this tick runs on a timer with nobody
                // watching, so on top of the OS/system-exec class it also refuses the DESTRUCTIVE
                // class and run-time-built SQL. Proved 2026-09-06: before this, a scheduled task
                // carrying DROP TABLE reached the server and executed.
                var guard = DangerousExecGuard.Inspect(task.Query, ExecSurfacePolicy.Unattended);
                if (!guard.IsAllowed)
                {
                    sw.Stop();
                    exec.Status = "Failed";
                    exec.ErrorMessage = $"Blocked by dangerous-exec guard: {guard.Reason}";
                    exec.DurationSeconds = sw.Elapsed.TotalSeconds;
                    exec.CompletedAt = DateTime.UtcNow;
                    _history.UpdateExecution(exec);

                    _toast.ShowError(task.Name, $"Blocked on {serverName}: {guard.Reason}", 6000);
                    _logger.LogWarning("Scheduled task '{Name}' blocked on {Server}: {Reason}",
                        task.Name, LogAnon.S(serverName), guard.Reason);

                    // Third surface for the same refusal, and the only one an auditor can find
                    // without knowing which task to open. The exec record is a row inside one
                    // task's history and the toast is gone in six seconds; a tick that nobody
                    // watched needs a durable, filterable line.
                    _audit?.LogUnattendedExecBlocked(
                        AuditLogService.ExecSurfaces.ScheduledTaskRun,
                        task.Name, serverName, task.Query, guard.Reason);
                    return;
                }
            }

            try
            {
                var connString = conn.GetConnectionString(serverName, task.Database);
                using var sqlConn = new SqlConnection(connString);
                await sqlConn.OpenAsync();

                using var cmd = new SqlCommand(task.Query, sqlConn)
                {
                    CommandTimeout = task.CommandTimeoutSeconds
                };

                using var reader = await cmd.ExecuteReaderAsync();
                using var dt = new DataTable();
                dt.Load(reader);

                sw.Stop();
                exec.RowCount = dt.Rows.Count;
                exec.DurationSeconds = sw.Elapsed.TotalSeconds;
                exec.CompletedAt = DateTime.UtcNow;
                // #68 LEG 3: exec.Status is finalized AFTER the delivery legs below run — a task
                // whose query succeeded but whose blob upload or email delivery failed must NOT
                // report plain "Success".
                var deliveryIssues = new List<string>();

                // CSV export
                if (task.Output.ExportCsv && dt.Rows.Count > 0)
                {
                    var csvPath = ExportToCsv(task, serverName, dt);
                    exec.CsvFilePath = csvPath;

                    // Azure Blob upload
                    if (task.Output.UploadToAzureBlob && _blobExport is { IsConfigured: true } && csvPath != null)
                    {
                        try
                        {
                            var result = await _blobExport.UploadLocalCsvAsync(csvPath, serverName);
                            if (result.Success)
                                exec.BlobUri = result.BlobUri;
                            else
                            {
                                deliveryIssues.Add($"Blob upload failed: {result.Message}");
                                _logger.LogWarning("Azure upload failed for task {TaskName}: {Message}", task.Name, result.Message);
                            }
                        }
                        catch (Exception blobEx)
                        {
                            deliveryIssues.Add($"Blob upload failed: {blobEx.Message}");
                            _logger.LogWarning(blobEx, "Azure upload failed for task {TaskName}", task.Name);
                        }
                    }
                }

                // Email notification
                if (task.Output.SendEmail && _notifications != null)
                {
                    try
                    {
                        var notification = BuildTaskCompletionNotification(
                            task.Name, task.Id, serverName, dt.Rows.Count, sw.Elapsed.TotalSeconds);
                        var dispatchResults = await _notifications.DispatchAsync(notification);
                        var emailFailures = dispatchResults.Where(r => !r.Success).ToList();
                        exec.EmailSent = dispatchResults.Any(r => r.Success);
                        if (emailFailures.Count > 0)
                        {
                            var detail = string.Join("; ", emailFailures.Select(f => $"{f.Channel}: {f.Detail}"));
                            deliveryIssues.Add($"Email: {detail}");
                            _logger.LogWarning("Email dispatch had failures for task {TaskName}: {Detail}", task.Name, detail);
                        }
                    }
                    catch (Exception emailEx)
                    {
                        deliveryIssues.Add($"Email dispatch threw: {emailEx.Message}");
                        _logger.LogWarning(emailEx, "Email dispatch failed for task {TaskName}", task.Name);
                    }
                }

                // Status is Success only when the query ran AND every requested delivery leg
                // succeeded; Warning (same honest-aggregate vocabulary as RestoreVerify) otherwise.
                exec.Status = deliveryIssues.Count > 0 ? "Warning" : "Success";
                if (deliveryIssues.Count > 0)
                    exec.ErrorMessage = string.Join(" | ", deliveryIssues);

                _history.UpdateExecution(exec);

                if (deliveryIssues.Count > 0)
                {
                    _toast.ShowError(task.Name,
                        $"{dt.Rows.Count} rows on {serverName} — delivery issue: {string.Join("; ", deliveryIssues)}", 8000);
                    _logger.LogWarning("Scheduled task '{Name}' completed on {Server} with delivery issues: {Issues}",
                        task.Name, LogAnon.S(serverName), string.Join(" | ", deliveryIssues));
                }
                else
                {
                    _toast.ShowSuccess(task.Name, $"{dt.Rows.Count} rows on {serverName} ({sw.Elapsed.TotalSeconds:F1}s)", 4000);
                    _logger.LogInformation("Scheduled task '{Name}' completed on {Server}: {Rows} rows in {Duration:F1}s",
                        task.Name, LogAnon.S(serverName), dt.Rows.Count, sw.Elapsed.TotalSeconds);
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                exec.Status = "Failed";
                exec.ErrorMessage = ex.Message;
                exec.DurationSeconds = sw.Elapsed.TotalSeconds;
                exec.CompletedAt = DateTime.UtcNow;
                _history.UpdateExecution(exec);

                _toast.ShowError(task.Name, $"Failed on {serverName}: {ex.Message}", 6000);
                _logger.LogWarning(ex, "Scheduled task '{Name}' failed on {Server}", task.Name, LogAnon.S(serverName));
            }
        }

        /// <summary>
        /// Runs the full enabled-check assessment for an Assessment-type task. Delegates to
        /// CheckExecutionService (which records check history and freezes the baseline on first
        /// run), then writes the scheduled-task execution record. Best-effort: a failure is
        /// logged + recorded, never thrown, so one bad server doesn't stop the cycle.
        /// </summary>
        private async Task ExecuteAssessmentOnServerAsync(
            ServerConnection conn, string serverName, ScheduledTaskExecution exec,
            System.Diagnostics.Stopwatch sw)
        {
            if (_checkExecution == null)
            {
                sw.Stop();
                exec.Status = "Failed";
                exec.ErrorMessage = "Assessment task requires the check-execution service, which is not available.";
                exec.DurationSeconds = sw.Elapsed.TotalSeconds;
                exec.CompletedAt = DateTime.UtcNow;
                _history.UpdateExecution(exec);
                _logger.LogWarning("Assessment task on {Server} skipped — CheckExecutionService not wired",
                    LogAnon.S(serverName));
                return;
            }

            try
            {
                var summary = await _checkExecution.ExecuteChecksAsync(conn, serverName, _cts.Token);

                sw.Stop();
                exec.RowCount = summary.TotalChecks;
                exec.DurationSeconds = sw.Elapsed.TotalSeconds;

                // A run that executed NOTHING is not a successful assessment (2026-08-05).
                // "Success" with RowCount 0 is a verdict conditioned on no measurement, and it is
                // the state a headless overnight run lands in when the check catalogue was never
                // loaded - the exact case the operator most needs told about, reported to them as
                // a clean pass. All three verdicts in this file now come from one tested place.
                (exec.Status, exec.ErrorMessage) = ScheduledRunVerdict.Assessment(
                    summary.TotalChecks, _checkExecution.CatalogueLoadError);

                exec.CompletedAt = DateTime.UtcNow;
                _history.UpdateExecution(exec);

                // Announce completion to any subscriber (a build-gated module may consume it via
                // its own fenced registration). Guarded so a subscriber fault can never fail,
                // delay, or alter this assessment run or its history — the record above is already
                // committed, and any subscriber does its own work off-thread.
                try
                {
                    AssessmentRunCompleted?.Invoke(this, new AssessmentRunCompletedEventArgs
                    {
                        ServerName = serverName,
                        CompletedAtUtc = DateTime.UtcNow,
                    });
                }
                catch (Exception subEx)
                {
                    _logger.LogWarning(subEx, "AssessmentRunCompleted subscriber threw on {Server}", LogAnon.S(serverName));
                }

                _toast.ShowSuccess("Scheduled assessment",
                    $"{serverName}: {summary.Passed} pass, {summary.Failed} fail ({sw.Elapsed.TotalSeconds:F1}s)", 4000);
                _logger.LogInformation(
                    "Scheduled assessment completed on {Server}: {Pass} pass, {Fail} fail in {Duration:F1}s",
                    LogAnon.S(serverName), summary.Passed, summary.Failed, sw.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                exec.Status = "Failed";
                exec.ErrorMessage = ex.Message;
                exec.DurationSeconds = sw.Elapsed.TotalSeconds;
                exec.CompletedAt = DateTime.UtcNow;
                _history.UpdateExecution(exec);

                _toast.ShowError("Scheduled assessment", $"Failed on {serverName}: {ex.Message}", 6000);
                _logger.LogWarning(ex, "Scheduled assessment failed on {Server}", LogAnon.S(serverName));
            }
        }

        /// <summary>
        /// MSP #9 — restore-test one server (VERIFYONLY-lite). Reads the most-recent full backup of each
        /// in-scope database from msdb, runs a striped-safe <c>RESTORE VERIFYONLY</c> per DB, and
        /// classifies every outcome honestly: Passed / Failed (backup corrupt = the real recoverability
        /// signal) / CouldNotRun (path unreadable, TDE cert missing, timeout) / NotSupported (URL/blob).
        /// Results are written to a per-server artifact (msdb records NO verify row) and the execution
        /// record; Status is "Failed" iff a corrupt backup was found, "Warning" iff some could not be
        /// checked, else "Success". Best-effort: a fault is logged + recorded, never thrown. §4.5:
        /// classification keys off SQL error NUMBERS only — no raw ex.Message, path, or secret is stored.
        /// </summary>
        private async Task ExecuteRestoreVerifyOnServerAsync(
            ScheduledTaskDefinition task, ServerConnection conn, string serverName,
            ScheduledTaskExecution exec, System.Diagnostics.Stopwatch sw)
        {
            try
            {
                var targets = RestoreVerifyService.QueryMostRecentFullTargets(
                    _connections, serverName,
                    task.RestoreVerify.IncludeSystemDatabases,
                    task.CommandTimeoutSeconds, _logger);

                if (task.RestoreVerify.MaxDatabasesPerRun > 0)
                    targets = targets.Take(task.RestoreVerify.MaxDatabasesPerRun).ToList();

                var results = new List<RestoreVerifyResult>();

                foreach (var target in targets)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    var (supported, supportReason) = RestoreVerifyService.IsSupported(target);
                    if (!supported)
                    {
                        results.Add(new RestoreVerifyResult(
                            target.DatabaseName, RestoreVerifyOutcome.NotSupported, supportReason,
                            target.BackupFinishDate, target.Stripes.Count, target.HasChecksums));
                        continue;
                    }

                    var statement = RestoreVerifyService.BuildVerifyStatement(target);
                    var (outcome, reason) = await RunSingleVerifyAsync(
                        conn, serverName, statement, task.CommandTimeoutSeconds, target.HasChecksums);
                    results.Add(new RestoreVerifyResult(
                        target.DatabaseName, outcome, reason,
                        target.BackupFinishDate, target.Stripes.Count, target.HasChecksums));
                }

                var nowUtc = DateTime.UtcNow;
                var path = RestoreVerifyService.SaveResults(serverName, results, nowUtc);

                var passed = results.Count(r => r.Outcome == RestoreVerifyOutcome.Passed);
                var failed = results.Count(r => r.Outcome == RestoreVerifyOutcome.Failed);
                var couldNot = results.Count(r => r.Outcome == RestoreVerifyOutcome.CouldNotRun);
                var notSupported = results.Count(r => r.Outcome == RestoreVerifyOutcome.NotSupported);

                sw.Stop();
                exec.RowCount = results.Count;
                exec.DurationSeconds = sw.Elapsed.TotalSeconds;
                exec.CompletedAt = DateTime.UtcNow;
                exec.CsvFilePath = path;   // reuse the output-path column for the results artifact

                // Honest aggregate: a corrupt backup is a real FAILURE; a could-not-check/omission is a
                // WARNING (NOT a clean pass); all-verified is Success. The FAILED-vs-COULD-NOT-RUN
                // distinction is fully itemised per DB in the results artifact.
                //
                // 2026-08-05: ZERO targets used to fall through this cascade to "Success" - this
                // file's own exemplar carried the same defect at its head as the two paths above
                // it, because "nothing failed" reads as a pass when nothing was tried.
                (exec.Status, exec.ErrorMessage) = ScheduledRunVerdict.RestoreVerify(
                    results.Count, passed, failed, couldNot, notSupported);

                _history.UpdateExecution(exec);

                if (failed > 0)
                    _toast.ShowError("Restore-verify",
                        $"{serverName}: {failed} backup(s) FAILED verify (corrupt)", 6000);
                else if (couldNot + notSupported > 0)
                    _toast.ShowInfo("Restore-verify",
                        $"{serverName}: {passed} passed, {couldNot + notSupported} not checked", 4000);
                else
                    _toast.ShowSuccess("Restore-verify",
                        $"{serverName}: {passed} backup(s) verified ({sw.Elapsed.TotalSeconds:F1}s)", 4000);

                _logger.LogInformation(
                    "Restore-verify on {Server}: {Pass} pass, {Fail} corrupt, {CouldNot} could-not-run, {NotSup} not-supported in {Duration:F1}s",
                    LogAnon.S(serverName), passed, failed, couldNot, notSupported, sw.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException)
            {
                throw;   // graceful shutdown — let the engine cycle unwind
            }
            catch (Exception ex)
            {
                sw.Stop();
                exec.Status = "Failed";
                // §4.5 honesty rail: fixed context + exception TYPE only — never ex.Message.
                exec.ErrorMessage = $"Restore-verify failed to run ({ex.GetType().Name}).";
                exec.DurationSeconds = sw.Elapsed.TotalSeconds;
                exec.CompletedAt = DateTime.UtcNow;
                _history.UpdateExecution(exec);

                _toast.ShowError("Restore-verify", $"Failed on {serverName}", 6000);
                _logger.LogWarning("Restore-verify failed on {Server} ({ExType})",
                    LogAnon.S(serverName), ex.GetType().Name);
            }
        }

        /// <summary>
        /// Run ONE RESTORE VERIFYONLY statement and classify the result. A clean run = Passed. A
        /// <see cref="SqlException"/> is classified by its error NUMBERS (corrupt-backup ⇒ Failed vs
        /// environment ⇒ CouldNotRun) — the raw message is never read. Any other fault ⇒ CouldNotRun.
        /// The Passed reason is QUALIFIED by whether the backup carried checksums: a WITH CHECKSUM verify
        /// validated content, but a NO_CHECKSUM verify is header/structure-only and can pass OVER content
        /// damage — so a structure-only pass must not read as content-verified.
        /// </summary>
        private async Task<(RestoreVerifyOutcome Outcome, string Reason)> RunSingleVerifyAsync(
            ServerConnection conn, string serverName, string statement, int commandTimeoutSeconds, bool hasChecksums)
        {
            try
            {
                using var sqlConn = new SqlConnection(conn.GetConnectionString(serverName, "master"));
                await sqlConn.OpenAsync(_cts.Token);
                using var cmd = new SqlCommand(statement, sqlConn)
                {
                    CommandTimeout = commandTimeoutSeconds > 0 ? commandTimeoutSeconds : 120
                };
                await cmd.ExecuteNonQueryAsync(_cts.Token);
                // Honest pass: only a WITH CHECKSUM verify actually validated the content. A backup taken
                // without checksums is verified WITH NO_CHECKSUM (structure/header only) — a pass there does
                // NOT rule out content damage, so say so rather than imply a content-verified pass.
                return hasChecksums
                    ? (RestoreVerifyOutcome.Passed, "Backup set verified (checksums validated).")
                    : (RestoreVerifyOutcome.Passed,
                        "Structure verified; no checksums to validate — this backup carries no checksums, " +
                        "so content damage cannot be ruled out.");
            }
            catch (SqlException sqlEx)
            {
                var classification = RestoreVerifyService.Classify(sqlEx);
                return (classification.Outcome, classification.Reason);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Non-SQL fault (e.g. connection open failure) — honestly a could-not-run, not a corrupt
                // verdict. Type only; never the message.
                return (RestoreVerifyOutcome.CouldNotRun,
                    $"Could not verify — a {ex.GetType().Name} prevented the verification from running.");
            }
        }

        private string? ExportToCsv(ScheduledTaskDefinition task, string serverName, DataTable dt)
        {
            try
            {
                var outputFolder = Path.Combine(AppContext.BaseDirectory, "output");
                if (!Directory.Exists(outputFolder))
                    Directory.CreateDirectory(outputFolder);

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var safeName = SanitizeFileName(task.Name);
                var safeServer = SanitizeFileName(serverName);
                var csvPath = Path.Combine(outputFolder, $"{safeServer}_{safeName}_{timestamp}.csv");

                var sb = new StringBuilder();

                // Headers
                var headers = dt.Columns.Cast<DataColumn>().Select(c => $"\"{c.ColumnName}\"");
                sb.AppendLine(string.Join(",", headers));

                // Rows
                foreach (DataRow row in dt.Rows)
                {
                    var values = dt.Columns.Cast<DataColumn>()
                        .Select(c => $"\"{row[c]?.ToString()?.Replace("\"", "\"\"") ?? ""}\"");
                    sb.AppendLine(string.Join(",", values));
                }

                File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
                _logger.LogInformation("CSV exported: {Path}", csvPath);
                return csvPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CSV export failed for task {Name}", task.Name);
                return null;
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unnamed";
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = name;
            foreach (var c in invalid) sanitized = sanitized.Replace(c, '_');
            return sanitized.Length > 80 ? sanitized[..80] : sanitized;
        }

        public void Dispose()
        {
            Stop();
            _timer?.Dispose();
            _cts?.Cancel();
            _cts?.Dispose();
            _evaluationLock?.Dispose();
        }

        /// <summary>
        /// The notification a COMPLETED SCHEDULED TASK sends. Extracted 2026-08-23 so a test can
        /// build it with production code rather than a hand-copied shape.
        ///
        /// <para>It is the counter-example that scoped N-2's wording: this sender is not a
        /// connectivity test and it measures no metric, so
        /// <see cref="AlertNotification.CurrentValueText"/> must print a phrase that names no
        /// cause. Leaving CurrentValue unset here is deliberate and is the honest state -- a task
        /// that returned rows crossed no threshold and read no counter.</para>
        /// </summary>
        internal static AlertNotification BuildTaskCompletionNotification(
            string taskName, string taskId, string serverName, int rowCount, double elapsedSeconds)
            => new()
            {
                AlertName = $"Scheduled Task: {taskName}",
                Metric = taskId,
                Severity = "info",
                InstanceName = serverName,
                Message = $"Task '{taskName}' completed on {serverName}: {rowCount} rows in {elapsedSeconds:F1}s"
            };

    }
}
