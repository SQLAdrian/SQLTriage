/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging;


#pragma warning disable CA1416 // Windows-only API — project targets net8.0-windows
namespace SQLTriage.Data.Services.Assessment
{
    /// <summary>
    /// Executes Registry-based assessment checks via Microsoft.Win32.Registry.
    /// </summary>
    internal static class RegistryCheckExecutor
    {
        public static CheckExecutionResult Run(
            AssessmentCheckDefinition check,
            string targetName,
            ILogger logger)
        {
            var result = new CheckExecutionResult { CheckId = check.CheckId, Passed = true };

            if (string.IsNullOrEmpty(check.Registry))
            {
                result.Skipped = true;
                return result;
            }

            // Registry format: HKEY_LOCAL_MACHINE\path\to\key
            var parts = check.Registry.Split(new[] { '\\' }, 2);
            if (parts.Length < 2)
            {
                result.Skipped = true;
                return result;
            }

            var subKeyPath = parts[1];

            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKeyPath);

            if (key != null)
            {
                result.Results.Add(SqlCheckExecutor.MakePassedResult(check, targetName));
            }
            else
            {
                result.Passed = false;
                result.Results.Add(new AssessmentResult
                {
                    CheckId            = check.CheckId,
                    Message            = $"Registry key {check.Registry} not found",
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

            return result;
        }
    }
}
#pragma warning restore CA1416
