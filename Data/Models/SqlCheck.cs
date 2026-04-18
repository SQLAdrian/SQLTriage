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

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; } = "Custom";

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "Warning";

        [JsonPropertyName("sqlQuery")]
        public string? SqlQuery { get; set; }

        [JsonPropertyName("expectedValue")]
        public int ExpectedValue { get; set; } = 0;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("recommendedAction")]
        public string? RecommendedAction { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("executionType")]
        public string? ExecutionType { get; set; }

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
    }
}
