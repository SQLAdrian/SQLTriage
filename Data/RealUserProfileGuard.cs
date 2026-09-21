/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Linq;

namespace SQLTriage.Data
{
    /// <summary>
    /// Refuses to bind a REAL per-user profile path when the process is a test host.
    ///
    /// <para><b>This is a runtime chokepoint, not a scan.</b> That distinction is the whole point.
    /// The lane this guard belongs to has now watched five static instruments be defeated, the last
    /// of them by an ordinary English sentence, and the standing lesson from that arc is to put the
    /// boundary where the behaviour actually happens and to demote anything lexical to a lint that
    /// says so. The behaviour here is "a constructor resolves
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> and opens the file it finds", and the
    /// only place that cannot be reworded around is the constructor itself.</para>
    ///
    /// <para><b>What went wrong.</b> Measured 2026-08-04: fourteen call sites across seven test
    /// files constructed <see cref="UserSettingsService"/> with its parameterless constructor, so
    /// the test suite bound the developer's real
    /// <c>%APPDATA%\SQLTriage\user-settings.json</c>. <c>LicenseServiceTests</c> and
    /// <c>ActivateFullAuditCardTests</c> called <c>ClearLicense()</c> and <c>TryActivate(...)</c>
    /// against it — writes. A fixture licence named <c>TEST_CLIENT_NEVER_PROD</c> was written into
    /// the operator's real profile and cleared again, and the file was observed oscillating between
    /// two states for an entire session. It went unnoticed for weeks, because a test that quietly
    /// reads and restores a real file leaves no failure behind.</para>
    ///
    /// <para><b>Why an environment variable cannot do this job.</b> Redirecting <c>%APPDATA%</c>
    /// does NOT sandbox .NET: <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
    /// resolves through the shell API and ignores the variable. A path parameter is the only seam
    /// that works, which is why every caller must pass one rather than arrange its environment.</para>
    ///
    /// <para><b>Escape hatch.</b> There is deliberately none by flag. A test that genuinely wants a
    /// real location passes that location explicitly — an act of intent, visible in the diff — and
    /// this guard never sees it, because it only fires on the DEFAULT path.</para>
    /// </summary>
    internal static class RealUserProfileGuard
    {
        /// <summary>
        /// True when an xunit assembly is loaded in this process — i.e. we are inside a test host.
        ///
        /// <para>Deliberately not cached. Assemblies load lazily, and a cached <c>false</c> computed
        /// early in a process that later loads xunit would be a guard that silently stopped
        /// guarding. These constructors run once or twice per process, so the enumeration is free.</para>
        /// </summary>
        internal static bool UnderTestHost =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                a.GetName().Name?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true);

        /// <summary>
        /// Throws when a test host is about to bind <paramref name="realPath"/>, a real per-user
        /// location. No-op in production, which is every process that is not a test host.
        /// </summary>
        /// <param name="service">The service binding the path, for the message.</param>
        /// <param name="realPath">The real path that was about to be bound.</param>
        /// <param name="seam">How the caller should have asked for a path instead.</param>
        internal static void RefuseRealProfileUnderTest(string service, string realPath, string seam)
        {
            if (!UnderTestHost) return;

            throw new InvalidOperationException(
                $"{service} was constructed under a test host with no path, so it was about to bind the "
                + $"REAL user profile at '{realPath}'. Tests must never read or write that file: it is the "
                + "operator's own settings, and it carries the install's License section (client name + "
                + "DPAPI-wrapped key). On 2026-08-04 this suite was measured writing a fixture licence "
                + "named TEST_CLIENT_NEVER_PROD into it and clearing it again on every run, unnoticed for "
                + "weeks.\n"
                + $"Use the path seam instead: {seam}\n"
                + "Note that redirecting %APPDATA% does NOT help — SpecialFolder.ApplicationData resolves "
                + "through the shell API and ignores the variable. The path parameter is the only seam.");
        }
    }
}
