/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.Assessment
{
    /// <summary>
    /// Executes SQL-based assessment checks against an open SqlConnection.
    /// Returns whether the check found issues (failed) or passed.
    /// </summary>
    internal static class SqlCheckExecutor
    {
        public static async Task<CheckExecutionResult> RunAsync(
            SqlConnection connection,
            AssessmentCheckDefinition check,
            string serverName,
            ILogger logger)
        {
            using var cmd = new SqlCommand(check.Sql, connection) { CommandTimeout = 30 };
            using var reader = await cmd.ExecuteReaderAsync();

            bool hasResults = false;
            var result = new CheckExecutionResult { CheckId = check.CheckId, Passed = true };

            while (await reader.ReadAsync())
            {
                hasResults = true;
                result.Passed = false;
                result.Results.Add(new AssessmentResult
                {
                    CheckId            = check.CheckId,
                    Message            = reader.IsDBNull(0) ? check.DisplayName : reader.GetString(0),
                    Severity           = check.Severity,
                    TargetName         = reader.IsDBNull(1) ? serverName : reader.GetString(1),
                    TargetType         = check.TargetType,
                    Category           = check.Category,
                    Description        = check.Description,
                    HelpLink           = check.HelpLink,
                    Status             = "Failed",
                    SqlQuery           = check.Sql,
                    ImplementationType = "Sql"
                });
            }
            reader.Close();

            if (!hasResults)
                result.Results.Add(MakePassedResult(check, serverName));

            return result;
        }

        internal static AssessmentResult MakePassedResult(AssessmentCheckDefinition check, string targetName) =>
            new()
            {
                CheckId            = check.CheckId,
                Message            = "Check passed - no issues found",
                Severity           = "Pass",
                TargetName         = targetName,
                TargetType         = check.TargetType,
                Category           = check.Category,
                Description        = check.Description,
                HelpLink           = check.HelpLink,
                Status             = "Passed",
                ImplementationType = "Sql"
            };
    }
}
