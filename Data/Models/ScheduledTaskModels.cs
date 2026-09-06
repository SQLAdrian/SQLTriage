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

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TaskType
    {
        SqlQuery,

        /// <summary>
        /// Runs the full enabled-check assessment against the target server(s) — the
        /// recurring re-scan that feeds the baseline timeline (P3). Unlike SqlQuery it
        /// ignores the Query field; the check set comes from the corpus.
        /// </summary>
        Assessment,

        /// <summary>
        /// MSP #5 — renders a branded executive/estate report (QuestPDF Executive Briefing)
        /// from the latest posture across the in-scope server(s), writes it to output\reports\,
        /// and (opt-in) emails it as a PDF attachment. Independently scheduled on its own cadence;
        /// it does NOT re-run checks and is NOT an AssessmentRunCompleted listener — it reflects
        /// whatever the most recent assessment left in the result store. Ignores the Query field.
        /// </summary>
        Report,

        /// <summary>
        /// MSP #9 — automated restore-test, VERIFYONLY-lite tier. Auto-generates a per-DB
        /// <c>RESTORE VERIFYONLY</c> for the MOST RECENT full backup of each online user database
        /// (paths read from msdb backupset⋈backupmediafamily, striped-backup safe), classifies every
        /// outcome as verify-PASSED / verify-FAILED (corrupt backup = the real recoverability signal)
        /// / verify-COULD-NOT-RUN (path unreadable, TDE cert missing, timeout) / not-supported
        /// (URL/Azure-blob), and writes a per-DB results artifact. Ignores the Query field. Opt-in:
        /// the operator must create the task. NOTE: RESTORE VERIFYONLY does NOT write an
        /// msdb.dbo.restorehistory row (empirically confirmed on SQL 2017/2022), so results are
        /// surfaced via THIS task's own execution record + the results artifact — never restorehistory.
        /// </summary>
        RestoreVerify
    }

    /// <summary>MSP #5 — which QuestPDF report a <see cref="TaskType.Report"/> task renders.
    /// Every value MUST map to a PURE-QuestPDF builder (no WebView2/PrintService path).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ReportKind
    {
        /// <summary>AssessmentPdf.BuildExecutiveBriefing — the exec-level score/posture narrative.</summary>
        ExecutiveBriefing
    }

    /// <summary>MSP #5 — config surface for a <see cref="TaskType.Report"/> task: which report and
    /// where it goes. Opt-in: a task with no recipients still writes the PDF to output\reports\ but
    /// sends no email. Content stays score/posture-level (design §4.5 — never raw query/plan text).</summary>
    public class TaskReportOptions
    {
        [JsonPropertyName("kind")]
        public ReportKind Kind { get; set; } = ReportKind.ExecutiveBriefing;

        /// <summary>Email recipients for the attached PDF. Empty = write-to-disk only (no email).</summary>
        [JsonPropertyName("recipients")]
        public List<string> Recipients { get; set; } = new();

        /// <summary>Optional company/branding line override; blank = fall back to the global report
        /// company-name setting.</summary>
        [JsonPropertyName("companyName")]
        public string CompanyName { get; set; } = string.Empty;
    }

    /// <summary>MSP #9 — config surface for a <see cref="TaskType.RestoreVerify"/> task. Lite tier:
    /// disk backups only, write-to-disk results (no email — avoids exfiltrating backup paths), opt-in.
    /// There is NO business-hours guard today — VERIFYONLY reads the backup files and can add I/O load,
    /// so schedule it off-peak (documented in the task help text). Respects CommandTimeoutSeconds per DB.</summary>
    public class TaskRestoreVerifyOptions
    {
        /// <summary>Include system databases (master/model/msdb) in the sweep. Default false —
        /// lite tier verifies ONLINE user databases (database_id &gt; 4) only.</summary>
        [JsonPropertyName("includeSystemDatabases")]
        public bool IncludeSystemDatabases { get; set; }

        /// <summary>Optional cap on how many databases to verify per server per run (0 = no cap).
        /// A guardrail for large estates so one run can't verify hundreds of backups back-to-back.</summary>
        [JsonPropertyName("maxDatabasesPerRun")]
        public int MaxDatabasesPerRun { get; set; }
    }

    public class ScheduledTaskDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("taskType")]
        public TaskType TaskType { get; set; } = TaskType.SqlQuery;

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

        /// <summary>MSP #5 — settings for a <see cref="TaskType.Report"/> task. Ignored by other types.</summary>
        [JsonPropertyName("report")]
        public TaskReportOptions Report { get; set; } = new();

        /// <summary>MSP #9 — settings for a <see cref="TaskType.RestoreVerify"/> task. Ignored by other types.</summary>
        [JsonPropertyName("restoreVerify")]
        public TaskRestoreVerifyOptions RestoreVerify { get; set; } = new();

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
