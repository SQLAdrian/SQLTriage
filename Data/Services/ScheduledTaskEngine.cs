/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data.Services
{
    public class ScheduledTaskEngine : IDisposable
    {
        private readonly ILogger<ScheduledTaskEngine> _logger;
        private readonly ScheduledTaskDefinitionService _definitions;
        private readonly ScheduledTaskHistoryService _history;
        private readonly ServerConnectionManager _connections;
        private readonly AzureBlobExportService? _blobExport;
        private readonly NotificationChannelService? _notifications;
        private readonly ToastService _toast;
        private readonly System.Timers.Timer _timer;

        private readonly SemaphoreSlim _evaluationLock = new(1, 1);
        private const int MaxConcurrentTasks = 3;
        private readonly SemaphoreSlim _taskSemaphore = new(MaxConcurrentTasks, MaxConcurrentTasks);

        private readonly ConcurrentDictionary<string, DateTime> _lastExecutionTime = new(StringComparer.OrdinalIgnoreCase);
        private bool _isRunning;

        public event Action? OnTaskCompleted;
        public bool IsRunning => _isRunning;

        public ScheduledTaskEngine(
            ILogger<ScheduledTaskEngine> logger,
            ScheduledTaskDefinitionService definitions,
            ScheduledTaskHistoryService history,
            ServerConnectionManager connections,
            ToastService toast,
            AzureBlobExportService? blobExport = null,
            NotificationChannelService? notifications = null)
        {
            _logger = logger;
            _definitions = definitions;
            _history = history;
            _connections = connections;
            _toast = toast;
            _blobExport = blobExport;
            _notifications = notifications;

            // Tick every 60 seconds, check which tasks are due
            _timer = new System.Timers.Timer(60_000);
            _timer.Elapsed += async (_, _) => await ExecuteAllDueAsync();
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

        public async Task ExecuteAllDueAsync()
        {
            if (!await _evaluationLock.WaitAsync(0)) return;

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
                        dueTasks.Add(ThrottledExecuteAsync(task));
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

        private async Task ThrottledExecuteAsync(ScheduledTaskDefinition task)
        {
            await _taskSemaphore.WaitAsync();
            try
            {
                await ExecuteTaskAsync(task);
            }
            finally
            {
                _taskSemaphore.Release();
            }
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
                var dt = new DataTable();
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
                    task.Name, serverName, dt.Rows.Count, sw.Elapsed.TotalSeconds);
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
                _logger.LogWarning(ex, "Scheduled task '{Name}' failed on {Server}", task.Name, serverName);
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
            _evaluationLock?.Dispose();
            _taskSemaphore?.Dispose();
        }
    }
}
