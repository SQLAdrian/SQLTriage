/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SqlHealthAssessment.Data.Services.Assessment
{
    /// <summary>
    /// Executes WMI-based assessment checks via ManagementObjectSearcher.
    /// </summary>
    internal static class WmiCheckExecutor
    {
        public static async Task<CheckExecutionResult> RunAsync(
            AssessmentCheckDefinition check,
            string targetName,
            ILogger logger)
        {
            var result = new CheckExecutionResult { CheckId = check.CheckId, Passed = true };

            if (string.IsNullOrEmpty(check.Wmi))
            {
                result.Skipped = true;
                return result;
            }

            using var searcher = new ManagementObjectSearcher(check.Wmi);
            var wmiResults = await Task.Run(() => searcher.Get());

            if (wmiResults.Count > 0)
            {
                var output = string.Join(Environment.NewLine,
                    wmiResults.Cast<ManagementObject>().Take(10).Select(m =>
                        string.Join(", ", m.Properties.Cast<PropertyData>()
                            .Select(p => $"{p.Name}={p.Value}"))));

                result.Passed = false;
                result.Results.Add(new AssessmentResult
                {
                    CheckId            = check.CheckId,
                    Message            = output,
                    Severity           = check.Severity,
                    TargetName         = targetName,
                    TargetType         = "LocalMachine",
                    Category           = check.Category,
                    Description        = check.Description,
                    HelpLink           = check.HelpLink,
                    SqlQuery           = check.Sql,
                    ImplementationType = check.ImplementationType,
                    Status             = "Failed"
                });
            }
            else
            {
                result.Results.Add(SqlCheckExecutor.MakePassedResult(check, targetName));
            }

            return result;
        }
    }
}
