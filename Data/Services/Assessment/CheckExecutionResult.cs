/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;

namespace SQLTriage.Data.Services.Assessment
{
    /// <summary>
    /// Result returned by each executor (Sql, PowerShell, Wmi, Registry).
    /// Carries the pass/fail state and the list of individual assessment results.
    /// </summary>
    internal class CheckExecutionResult
    {
        public string CheckId { get; set; } = "";
        public bool Passed { get; set; } = true;
        public bool Skipped { get; set; } = false;
        public List<AssessmentResult> Results { get; } = new();
    }
}
