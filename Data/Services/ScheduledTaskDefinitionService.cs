/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:ScheduledTaskDefinitionService.Class — loads and persists scheduled task definitions from JSON
    public class ScheduledTaskDefinitionService
    {
        private readonly ILogger<ScheduledTaskDefinitionService> _logger;
        private readonly AuditLogService? _audit;
        private readonly string _filePath;
        private readonly object _lock = new();
        private ScheduledTasksFile _definitions = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public event Action? OnDefinitionsChanged;

        /// <summary>
        /// How scheduled-tasks.json came off disk.
        ///
        /// <para><b>ANNOUNCED with a quarantine, not refused.</b> Every field in this store was
        /// typed by an operator — a name, a target server, a database, a query, a time of day, an
        /// export checkbox (<c>ScheduledTaskDefinition</c> carries no credential; the SQL login
        /// comes from server-connections.json, and the blob SAS from portal-settings.json, both of
        /// which refuse). It is the finding-owners.json shape: losing it costs an afternoon of
        /// re-entry, not a secret and not a control.</para>
        ///
        /// <para><b>Driven by the 2026-08-04 cold gate on this exact service:</b> with the store
        /// damaged, one <c>AddTask</c> destroyed an operator's "Nightly assessment", the file's hash
        /// moved, and there was no quarantine and no line anywhere saying it had happened. The tier
        /// is right; the silence was not. Both are what changed here — the loss is now stated when
        /// it happens, and the <c>.rejected-</c> copy makes the schedules recoverable.</para>
        /// </summary>
        private ConfigLoadOutcome _load = ConfigLoadOutcome.Missing;

        private string? _quarantine;

        /// <summary>True when scheduled-tasks.json exists and did not load, so no task is scheduled.</summary>
        public bool IsStoreDamaged { get { lock (_lock) return _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty; } }

        /// <summary>What to do about it. The ONE register — never a locally written sentence.</summary>
        public string DescribeStoreRecovery()
        {
            lock (_lock) return ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine);
        }

        public ScheduledTaskDefinitionService(ILogger<ScheduledTaskDefinitionService> logger, AuditLogService? audit = null)
            : this(logger, null, audit) { }

        /// <param name="tasksFilePath">
        /// Test seam (house pattern): null ⇒ the real Config/ path, exactly as before.
        /// </param>
        /// <param name="audit">
        /// Optional so minimal DI/test graphs still construct; the DI singleton supplies it, so a
        /// real save-time refusal is journaled.
        /// </param>
        internal ScheduledTaskDefinitionService(ILogger<ScheduledTaskDefinitionService> logger, string? tasksFilePath, AuditLogService? audit = null)
        {
            _logger = logger;
            _audit = audit;
            if (tasksFilePath != null)
            {
                _filePath = tasksFilePath;
            }
            else
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                _filePath = Path.Combine(baseDir, "config", "scheduled-tasks.json");
                if (!File.Exists(_filePath))
                    _filePath = Path.Combine(baseDir, "Config", "scheduled-tasks.json");
            }
            Load();
        }

        public List<ScheduledTaskDefinition> GetAllTasks()
        {
            lock (_lock) return _definitions.Tasks.ToList();
        }

        public List<ScheduledTaskDefinition> GetEnabledTasks()
        {
            lock (_lock) return _definitions.Tasks.Where(t => t.Enabled).ToList();
        }

        public ScheduledTaskDefinition? GetTask(string taskId)
        {
            lock (_lock) return _definitions.Tasks.FirstOrDefault(t => t.Id == taskId);
        }

        public TaskSaveResult AddTask(ScheduledTaskDefinition task)
        {
            // exec-surface-safety: a SqlQuery task's free-form T-SQL is gated per-statement for the
            // OS/system-exec class before it is ever persisted or scheduled. On a block the task is
            // rejected (not written) and the reason is returned for the editor to surface.
            var blockedReason = GuardTaskQuery(task);
            if (blockedReason != null)
            {
                _logger.LogWarning("Scheduled task rejected at save: {Name} blocked: {Reason}", task.Name, blockedReason);
                return TaskSaveResult.Blocked(blockedReason);
            }

            lock (_lock)
            {
                task.CreatedAt = DateTime.UtcNow;
                task.LastModifiedAt = DateTime.UtcNow;
                _definitions.Tasks.Add(task);
                Save();
            }
            _logger.LogInformation("Scheduled task added: {Name} ({Id})", task.Name, task.Id);
            OnDefinitionsChanged?.Invoke();
            return TaskSaveResult.Ok();
        }

        public TaskSaveResult UpdateTask(ScheduledTaskDefinition task)
        {
            var blockedReason = GuardTaskQuery(task);
            if (blockedReason != null)
            {
                _logger.LogWarning("Scheduled task rejected at save: {Name} blocked: {Reason}", task.Name, blockedReason);
                return TaskSaveResult.Blocked(blockedReason);
            }

            lock (_lock)
            {
                var index = _definitions.Tasks.FindIndex(t => t.Id == task.Id);
                if (index >= 0)
                {
                    task.LastModifiedAt = DateTime.UtcNow;
                    _definitions.Tasks[index] = task;
                    Save();
                }
            }
            OnDefinitionsChanged?.Invoke();
            return TaskSaveResult.Ok();
        }

        /// <summary>
        /// Runs the dangerous-exec guard over a task's query. Only a SqlQuery task carries free-form
        /// T-SQL; the other task types ignore the Query field, so they are never gated. Returns the
        /// block reason, or null when the task is allowed.
        /// </summary>
        private string? GuardTaskQuery(ScheduledTaskDefinition task)
        {
            if (task.TaskType != TaskType.SqlQuery)
                return null;

            // ExecSurfacePolicy.Unattended (2026-09-07): a saved task is a standing instruction that
            // will later execute on a timer with nobody watching, so the save path judges it under
            // exactly the policy the tick will use. Refusing at save is the kinder half of the same
            // gate — the operator is here, reading the reason, instead of finding a Failed row days
            // later. ScheduledTaskEngine.cs applies the identical policy at the tick.
            var verdict = DangerousExecGuard.Inspect(task.Query, ExecSurfacePolicy.Unattended);
            if (verdict.IsAllowed)
                return null;

            _audit?.LogUnattendedExecBlocked(
                AuditLogService.ExecSurfaces.ScheduledTaskSave,
                task.Name, task.ServerName, task.Query, verdict.Reason);

            return verdict.Reason;
        }

        public void DeleteTask(string taskId)
        {
            lock (_lock)
            {
                _definitions.Tasks.RemoveAll(t => t.Id == taskId);
                Save();
            }
            _logger.LogInformation("Scheduled task deleted: {Id}", taskId);
            OnDefinitionsChanged?.Invoke();
        }

        public void SetTaskEnabled(string taskId, bool enabled)
        {
            lock (_lock)
            {
                var task = _definitions.Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task != null)
                {
                    task.Enabled = enabled;
                    Save();
                }
            }
            OnDefinitionsChanged?.Invoke();
        }

        private void Load()
        {
            try
            {
                _definitions = ConfigFileHelper.Load<ScheduledTasksFile>(
                    _filePath, JsonOptions, out _load, out _quarantine);

                if (IsStoreDamaged)
                    _logger.LogError(
                        "[ScheduledTasks] {Path} exists and did not load ({Outcome}). NO scheduled task is "
                        + "registered in this process — nothing will run on a schedule until it is repaired. The "
                        + "next task you add or change will replace the file with just that one, so repair it "
                        + "first if you want the rest back. {Recovery}",
                        _filePath, _load,
                        ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
                else
                    _logger.LogInformation("Loaded {Count} scheduled task definitions", _definitions.Tasks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load scheduled task definitions from {Path}", _filePath);
                _definitions = new();
                _load = ConfigLoadOutcome.Unreadable;
            }
        }

        private void Save()
        {
            // Announced, not refused — see _load. Fires exactly once per damaged load, because a
            // successful write makes the store Loaded, and it is the only record of the moment the
            // damaged file stopped existing.
            var replacingDamage = _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty;

            try
            {
                ConfigFileHelper.Save(_filePath, _definitions, JsonOptions);

                if (replacingDamage)
                    _logger.LogWarning(
                        "[ScheduledTasks] {Path} did not load ({Outcome}) and has now been REPLACED by the {Count} "
                        + "task(s) held in memory. Every schedule the damaged file held is gone from the live file. "
                        + "{Recovery}",
                        _filePath, _load, _definitions.Tasks.Count,
                        ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));

                _load = ConfigLoadOutcome.Loaded;
                _quarantine = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save scheduled task definitions");
            }
        }
    }

    /// <summary>
    /// Outcome of an AddTask/UpdateTask call. A task whose query the dangerous-exec guard blocks is
    /// NOT persisted; <see cref="Saved"/> is false and <see cref="BlockedReason"/> carries the reason
    /// for the editor to show. Existing callers that ignore the return value keep compiling.
    /// </summary>
    public class TaskSaveResult
    {
        public bool Saved { get; private set; }
        public string? BlockedReason { get; private set; }

        public static TaskSaveResult Ok() => new() { Saved = true };
        public static TaskSaveResult Blocked(string reason) => new() { Saved = false, BlockedReason = reason };
    }
}
