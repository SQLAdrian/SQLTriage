/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SQLTriage.Data.Models
{
    public class ScheduledTasksFile
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        [JsonPropertyName("tasks")]
        public List<ScheduledTaskDefinition> Tasks { get; set; } = new();
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ScheduleType
    {
        Daily,
        Weekly,
        Monthly,
        CustomInterval
    }

    public class TaskSchedule
    {
        [JsonPropertyName("type")]
        public ScheduleType Type { get; set; } = ScheduleType.Daily;

        /// <summary>Time of day in HH:mm format (local time).</summary>
        [JsonPropertyName("timeOfDay")]
        public string TimeOfDay { get; set; } = "02:00";

        /// <summary>Day of week for Weekly schedule.</summary>
        [JsonPropertyName("dayOfWeek")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DayOfWeek? DayOfWeek { get; set; }

        /// <summary>Day of month (1-28) for Monthly schedule.</summary>
        [JsonPropertyName("dayOfMonth")]
        public int? DayOfMonth { get; set; }

        /// <summary>Interval in minutes for CustomInterval schedule.</summary>
        [JsonPropertyName("intervalMinutes")]
        public int? IntervalMinutes { get; set; }
    }

    public class TaskOutputOptions
    {
        [JsonPropertyName("exportCsv")]
        public bool ExportCsv { get; set; }

        [JsonPropertyName("uploadToAzureBlob")]
        public bool UploadToAzureBlob { get; set; }

        [JsonPropertyName("sendEmail")]
        public bool SendEmail { get; set; }
    }

    public class ScheduledTaskDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>Specific server to run against, or empty for all enabled servers.</summary>
        [JsonPropertyName("serverName")]
        public string ServerName { get; set; } = string.Empty;

        [JsonPropertyName("database")]
        public string Database { get; set; } = "master";

        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("commandTimeoutSeconds")]
        public int CommandTimeoutSeconds { get; set; } = 120;

        [JsonPropertyName("schedule")]
        public TaskSchedule Schedule { get; set; } = new();

        [JsonPropertyName("output")]
        public TaskOutputOptions Output { get; set; } = new();

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("lastModifiedAt")]
        public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;
    }

    public class ScheduledTaskExecution
    {
        public long Id { get; set; }
        public string TaskId { get; set; } = string.Empty;
        public string TaskName { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string Status { get; set; } = "Running";
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int RowCount { get; set; }
        public string? CsvFilePath { get; set; }
        public string? BlobUri { get; set; }
        public bool EmailSent { get; set; }
        public string? ErrorMessage { get; set; }
        public double DurationSeconds { get; set; }
    }
}
