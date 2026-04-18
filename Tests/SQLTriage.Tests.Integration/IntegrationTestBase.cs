/* In the name of God, the Merciful, the Compassionate */
using Xunit;

namespace SQLTriage.Tests.Integration;

/// <summary>
/// Base for all integration tests. Skips automatically unless SQLTRIAGE_INTEGRATION=1.
/// Requires a live SQL Server (LocalDB acceptable).
/// Run with: $env:SQLTRIAGE_INTEGRATION=1; dotnet test Tests/SQLTriage.Tests.Integration
/// </summary>
public abstract class IntegrationTestBase
{
    protected static readonly bool IsEnabled =
        Environment.GetEnvironmentVariable("SQLTRIAGE_INTEGRATION") == "1";

    protected static void SkipIfDisabled()
    {
        if (!IsEnabled)
            throw new SkipException("Set SQLTRIAGE_INTEGRATION=1 to run integration tests.");
    }
}

/// <summary>Lightweight skip mechanism without external package dependency.</summary>
public sealed class SkipException(string reason) : Exception(reason);
