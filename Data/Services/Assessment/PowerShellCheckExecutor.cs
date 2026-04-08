/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SqlHealthAssessment.Data.Services.Assessment
{
    /// <summary>
    /// Executes PowerShell-based assessment checks via powershell.exe.
    /// </summary>
    internal static class PowerShellCheckExecutor
    {
        public static async Task<CheckExecutionResult> RunAsync(
            AssessmentCheckDefinition check,
            string targetName,
            ILogger logger)
        {
            var result = new CheckExecutionResult { CheckId = check.CheckId, Passed = true };

            if (string.IsNullOrEmpty(check.PowerShell))
            {
                result.Skipped = true;
                return result;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = $"-NoProfile -ExecutionPolicy Bypass -Command \"{check.PowerShell}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                result.Skipped = true;
                return result;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error  = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrEmpty(error))
                logger.LogWarning("PowerShell check {CheckId} stderr: {Error}", check.CheckId, error);

            if (!string.IsNullOrWhiteSpace(output))
            {
                result.Passed = false;
                result.Results.Add(new AssessmentResult
                {
                    CheckId            = check.CheckId,
                    Message            = output.Trim(),
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
