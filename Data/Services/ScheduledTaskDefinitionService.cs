/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    public class ScheduledTaskDefinitionService
    {
        private readonly ILogger<ScheduledTaskDefinitionService> _logger;
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

        public ScheduledTaskDefinitionService(ILogger<ScheduledTaskDefinitionService> logger)
        {
            _logger = logger;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _filePath = Path.Combine(baseDir, "config", "scheduled-tasks.json");
            if (!File.Exists(_filePath))
                _filePath = Path.Combine(baseDir, "Config", "scheduled-tasks.json");
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

        public void AddTask(ScheduledTaskDefinition task)
        {
            lock (_lock)
            {
                task.CreatedAt = DateTime.UtcNow;
                task.LastModifiedAt = DateTime.UtcNow;
                _definitions.Tasks.Add(task);
                Save();
            }
            _logger.LogInformation("Scheduled task added: {Name} ({Id})", task.Name, task.Id);
            OnDefinitionsChanged?.Invoke();
        }

        public void UpdateTask(ScheduledTaskDefinition task)
        {
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
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                        _definitions = JsonSerializer.Deserialize<ScheduledTasksFile>(json, JsonOptions) ?? new();
                }
                _logger.LogInformation("Loaded {Count} scheduled task definitions", _definitions.Tasks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load scheduled task definitions from {Path}", _filePath);
                _definitions = new();
            }
        }

        private void Save()
        {
            try
            {
                ConfigFileHelper.Save(_filePath, _definitions, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save scheduled task definitions");
            }
        }
    }
}
