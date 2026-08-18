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
        private readonly System.Timers.Timer _timer;

        private readonly SemaphoreSlim _evaluationLock = new(1, 1);

        private readonly ConcurrentDictionary<string, DateTime> _lastExecutionTime = new(StringComparer.OrdinalIgnoreCase);
        private bool _isRunning;
        private readonly CancellationTokenSource _cts = new();

        public event Action? OnTaskCompleted;
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
            CheckExecutionService? checkExecution = null)
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
            _isRunning = false;
            _timer.Stop();
            _cts.Cancel();
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

            foreach (var conn in serverConnections)
            {
                var servers = string.IsNullOrEmpty(task.ServerName)
                    ? conn.GetServerList()
                    : new List<string> { task.ServerName };

                foreach (var serverName in servers)
                {
                    await ExecuteOnServerAsync(task, conn, serverName);
                }
            }

            _lastExecutionTime[task.Id] = DateTime.Now;
            OnTaskCompleted?.Invoke();
        }

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
                exec.Status = "Success";
                exec.CompletedAt = DateTime.UtcNow;

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
                        }
                        catch (Exception blobEx)
                        {
                            _logger.LogWarning(blobEx, "Azure upload failed for task {TaskName}", task.Name);
                        }
                    }
                }

                // Email notification
                if (task.Output.SendEmail && _notifications != null)
                {
                    try
                    {
                        var notification = new AlertNotification
                        {
                            AlertName = $"Scheduled Task: {task.Name}",
                            Metric = task.Id,
                            Severity = "info",
                            InstanceName = serverName,
                            Message = $"Task '{task.Name}' completed on {serverName}: {dt.Rows.Count} rows in {sw.Elapsed.TotalSeconds:F1}s"
                        };
                        await _notifications.DispatchAsync(notification);
                        exec.EmailSent = true;
                    }
                    catch (Exception emailEx)
                    {
                        _logger.LogWarning(emailEx, "Email dispatch failed for task {TaskName}", task.Name);
                    }
                }

                _history.UpdateExecution(exec);
                _toast.ShowSuccess(task.Name, $"{dt.Rows.Count} rows on {serverName} ({sw.Elapsed.TotalSeconds:F1}s)", 4000);
                _logger.LogInformation("Scheduled task '{Name}' completed on {Server}: {Rows} rows in {Duration:F1}s",
                    task.Name, LogAnon.S(serverName), dt.Rows.Count, sw.Elapsed.TotalSeconds);
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
                exec.Status = "Success";
                exec.CompletedAt = DateTime.UtcNow;
                _history.UpdateExecution(exec);

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
    }
}
