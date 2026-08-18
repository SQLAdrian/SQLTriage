/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// Represents a SQLWATCH check configuration stored in sql-checks.json
    /// </summary>
    public class SqlCheck
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("display_id")]
        public string? DisplayId { get; set; }

        [JsonIgnore]
        public string SafeDisplayId => !string.IsNullOrWhiteSpace(DisplayId) ? DisplayId : Id;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Client-facing business-impact prose (the corpus '## Business Impact' section).
        /// Distinct from <see cref="Description"/> (the '## Intent' / DBA voice, which may carry
        /// oracle-derivation notes). Reports render this for the business audience.
        /// </summary>
        [JsonPropertyName("business_impact")]
        public string? BusinessImpact { get; set; }

        [JsonPropertyName("eli5_description")]
        public string? Eli5Description { get; set; }

        [JsonPropertyName("eli5_remediation")]
        public string? Eli5Remediation { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; } = "Custom";

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "Warning";

        [JsonPropertyName("sqlQuery")]
        public string? SqlQuery { get; set; }

        [JsonPropertyName("expectedValue")]
        public int ExpectedValue { get; set; }

        [JsonPropertyName("effortHours")]
        public double EffortHours { get; set; }

        [JsonPropertyName("isBad")]
        public bool IsBad { get; set; }

        [JsonPropertyName("scoreWeight")]
        public int ScoreWeight { get; set; } = 1;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("recommendedAction")]
        public string? RecommendedAction { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        // Multi-source upstream attribution. A check may derive from / be
        // equivalent to multiple upstream tools or authorities (e.g.
        // ["sp_BLITZ", "Brent Ozar Unlimited", "CIS SQL Server Benchmark"]).
        // SqlCheckBuilder populates this from YAML `sources:` (sequence); when
        // only the scalar `source:` is present, it's auto-promoted into this
        // list so consumers can read uniformly. See
        // .handoff/BRIEF_corpus_multi_source_for_DeepSeek.md.
        [JsonPropertyName("sources")]
        public List<string> Sources { get; set; } = new();

        [JsonPropertyName("executionType")]
        public string? ExecutionType { get; set; }

        /// <summary>
        /// How the check runs. Default/empty = "tsql" (a SqlQuery executed on the SQL connection).
        /// "host-probe" = an OS/AD probe via <see cref="HostProbeKey"/> instead of SQL — runs through
        /// the fail-closed, bundle-gated HostProbeService (no SQL connection). See the host-probe brief.
        /// </summary>
        [JsonPropertyName("method")]
        public string? Method { get; set; }

        /// <summary>
        /// For Method == "host-probe": which probe to run (e.g. "host.powerplan"). Maps to a
        /// HostProbeService probe key; ignored for T-SQL checks.
        /// </summary>
        [JsonPropertyName("probeKey")]
        public string? ProbeKey { get; set; }

        [JsonPropertyName("rowCountCondition")]
        public string? RowCountCondition { get; set; }

        [JsonPropertyName("resultInterpretation")]
        public string? ResultInterpretation { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; } = 1;

        [JsonPropertyName("severityScore")]
        public int SeverityScore { get; set; } = 1;

        [JsonPropertyName("weight")]
        public double Weight { get; set; } = 0.0;

        [JsonPropertyName("expectedState")]
        public string? ExpectedState { get; set; }

        [JsonPropertyName("checkTriggered")]
        public string? CheckTriggered { get; set; }

        [JsonPropertyName("checkCleared")]
        public string? CheckCleared { get; set; }

        [JsonPropertyName("detailedRemediation")]
        public string? DetailedRemediation { get; set; }

        [JsonPropertyName("supportType")]
        public string? SupportType { get; set; }

        [JsonPropertyName("impactScore")]
        public int ImpactScore { get; set; } = 3;

        [JsonPropertyName("additionalNotes")]
        public string? AdditionalNotes { get; set; }

        // ── Playbook / narrative enrichment fields (from YAML bundle) ──
        // These are optional — populated when the corpus YAML includes
        // Round 6 narrative enrichment. Null-safe downstream.

        [JsonPropertyName("includeInMainActions")]
        public bool? IncludeInMainActions { get; set; }

        [JsonPropertyName("fullStoryAction")]
        public string? FullStoryAction { get; set; }

        [JsonPropertyName("nextAction")]
        public string? NextAction { get; set; }

        [JsonPropertyName("checkTriggeredSimplified")]
        public string? CheckTriggeredSimplified { get; set; }

        [JsonPropertyName("checkClearedSimplified")]
        public string? CheckClearedSimplified { get; set; }

        // Legacy IDs from upstream tools (e.g. ["BLITZ_001"] linking a SQLTriage
        // check back to its sp_BLITZ CheckID) — provenance for corpus checks
        // derived from upstream sources.
        [JsonPropertyName("legacyIds")]
        public List<string> LegacyIds { get; set; } = new();

        // X-1 (pre-mortem FM-3, 2026-06-01): SQL Server EngineEdition values on which this
        // check is valid (SERVERPROPERTY('EngineEdition'): 1=Personal, 2=Standard,
        // 3=Enterprise, 4=Express, 5=Azure SQL DB, 6=Azure Synapse, 8=Azure Managed
        // Instance, 9=Azure SQL Edge, 11=Synapse serverless). EMPTY = no restriction
        // (valid on all editions) — the default, so untagged checks behave exactly as
        // before. CheckExecutionService uses it to SKIP (not error) a check on an engine
        // it cannot run on, closing the Azure version-trap where on-prem-only checks
        // (master/msdb/OS) hard-error on Azure SQL DB after a version gate wrongly passed.
        [JsonPropertyName("supportedEngineEditions")]
        public List<int> SupportedEngineEditions { get; set; } = new();
    }
}
