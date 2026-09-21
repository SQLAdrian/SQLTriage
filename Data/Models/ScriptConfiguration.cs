/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SQLTriage.Data.Models
{
    public class ScriptConfiguration
    {
        [JsonPropertyName("Id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("ScriptPath")]
        public string ScriptPath { get; set; } = string.Empty;

        /// <summary>
        /// Optional SQL query run before ExecutionParameters. Must return a single row
        /// with a "ToRun" column (0 or 1). If ToRun = 0, ExecutionParameters is skipped
        /// but the .sql script and SqlQueryForOutput (when ExportToCsv is true) still run.
        /// If empty, ExecutionParameters always runs.
        /// </summary>
        [JsonPropertyName("ExecutionTest")]
        public string ExecutionTest { get; set; } = string.Empty;

        [JsonPropertyName("ExecutionParameters")]
        public string ExecutionParameters { get; set; } = string.Empty;

        [JsonPropertyName("SqlQueryForOutput")]
        public string SqlQueryForOutput { get; set; } = string.Empty;

        [JsonPropertyName("Enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("TimeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 900;

        [JsonPropertyName("Category")]
        public string Category { get; set; } = "Diagnostic";

        [JsonPropertyName("ExecutionOrder")]
        public int ExecutionOrder { get; set; }

        [JsonPropertyName("ExportToCsv")]
        public bool ExportToCsv { get; set; }

        /// <summary>
        /// Which result set of <see cref="SqlQueryForOutput"/> is the one to capture and export,
        /// counting from 0. Default 0 is what every entry did before this property existed: take
        /// the first set and ignore anything after it.
        ///
        /// <para>WHY IT EXISTS. sp_PerfCheck has no @OutputTableName, so the only way to read its
        /// findings is to EXEC it and take the rows off the wire, and it returns TWO sets: a
        /// two-column [Server Information] / [Details] banner first, then the nine-column findings.
        /// Capturing set 0 would export the banner under the script's name and call it the audit.
        /// DiagnosticScriptRunner advances the reader this many times and FAILS THE RUN if the set
        /// is not there, rather than falling back to set 0 with the wrong columns.</para>
        /// </summary>
        [JsonPropertyName("OutputResultSetIndex")]
        public int OutputResultSetIndex { get; set; }

        /// <summary>
        /// True when this script returning zero rows is a healthy answer rather than a problem.
        /// Default false keeps the existing behaviour for every entry that predates this property.
        ///
        /// <para>WHY IT EXISTS. Full Audit raises a "No-Rows" issue after a run when a CSV-exporting
        /// script reports success with 0 rows, because for sp_Blitz and sp_triage that means the
        /// output query read nothing and the CSV is empty. sp_PerfCheck is the opposite: it emits a
        /// findings row per problem found, so an instance with nothing wrong legitimately returns
        /// none. Without this flag a healthy server would be reported to the operator as a fault in
        /// the tooling every single run, which trains people to ignore the modal.</para>
        /// </summary>
        [JsonPropertyName("EmptyResultIsNormal")]
        public bool EmptyResultIsNormal { get; set; }
    }

    public class ScriptExecutionResult
    {
        public string ScriptName { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? StatusMessage { get; set; }

        /// <summary>
        /// True when this run's ExecutionTest returned ToRun=0, so no EXEC happened and the rows
        /// exported are the PREVIOUS execution's. The path is deliberate and the data is not wrong:
        /// the CSV carries the real, older CheckDate and both readers take the audit date from that
        /// column, so the dashboard and roadmap show the true audit date.
        ///
        /// <para>What was wrong is that nothing said so. The success log line read "executed
        /// successfully" when nothing executed, and the post-run modal raised nothing at all, because
        /// Success was true, ErrorMessage was empty and RowsAffected was positive. StatusMessage
        /// carried "Loaded previous execution" and has three readers, so the operator did see it on
        /// two screens and in the summary CSV. A flag rather than a string match, so the log line and
        /// the modal test the fact and not the wording of a label.</para>
        /// </summary>
        public bool ReusedPreviousExecution { get; set; }

        public int RowsAffected { get; set; }
        public List<Dictionary<string, object>>? Results { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }
}
