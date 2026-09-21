/* In the name of God, the Merciful, the Compassionate */

using System.Runtime.InteropServices;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Creates the Windows service by calling CreateServiceW directly.
    ///
    /// WHY NOT sc.exe. sc.exe takes the service-account password as a <c>password= "..."</c> token on
    /// its own command line, and a process command line is readable by any same-user process. That is
    /// not a theoretical exposure - it was reproduced on this machine with an UNELEVATED
    /// Get-CimInstance Win32_Process query returning the full argument list including the password.
    /// sc.exe has no stdin channel and no other way in, so there is no secret-safe way to keep it for
    /// the create step. CreateServiceW takes the password as a pointer, which never crosses a
    /// process boundary.
    ///
    /// SCOPE. Only the create step moves to P/Invoke. The description and failure-recovery calls that
    /// follow it carry NO secret and already work through sc.exe, so they stay there: every extra
    /// native call is marshalling that cannot be exercised without an elevated run, and adding
    /// unexercisable surface to fix an exposure that is already closed would be a bad trade. If you
    /// want those moved too, that is a deliberate follow-up, not an oversight.
    /// </summary>
    internal static class Win32ServiceInstaller
    {
        // Access rights
        internal const uint SC_MANAGER_CONNECT = 0x0001;
        internal const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
        private const uint SERVICE_ALL_ACCESS = 0xF01FF;

        /// <summary>The access mask production installs open the SCM with.</summary>
        internal const uint DefaultScmAccess = SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE;

        // Service type / start type / error control
        private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
        private const uint SERVICE_AUTO_START = 0x00000002;
        private const uint SERVICE_ERROR_NORMAL = 0x00000001;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateServiceW(
            IntPtr hSCManager,
            string lpServiceName,
            string lpDisplayName,
            uint dwDesiredAccess,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string? lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string? lpDependencies,
            string? lpServiceStartName,
            IntPtr lpPassword);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        /// <summary>
        /// Creates the service. <paramref name="passwordPtr"/> is an unmanaged null-terminated UTF-16
        /// buffer, or IntPtr.Zero for accounts that take no password (LocalSystem and the
        /// NT AUTHORITY built-ins). Returns 0 on success, otherwise the Win32 error from the API.
        /// </summary>
        /// <param name="scmAccess">
        /// Test seam (InternalsVisibleTo SQLTriage.Tests). Production always uses
        /// <see cref="DefaultScmAccess"/>. A test passes SC_MANAGER_CONNECT alone, which is grantable
        /// without elevation but carries no create right, so the REAL CreateServiceW below runs while
        /// being structurally incapable of creating a service. Declaring a second copy of the
        /// signature inside the test would only have proved the copy.
        ///
        /// <para>What that seam DOES demonstrate: this exact P/Invoke declaration binds, the CLR
        /// marshals these arguments to advapi32 without throwing, and the call returns a Win32 error
        /// rather than a crash - so the calling convention, the CharSet and the argument count are
        /// right.</para>
        ///
        /// <para>What it does NOT demonstrate, and what an earlier version of this comment wrongly
        /// claimed as "validated by Windows end to end": that the VALUES are right. Under a handle
        /// without create rights the API fails ACCESS_DENIED, and that check precedes parameter
        /// validation - so the seam returns the same result for a wrong dwServiceType, a wrong
        /// dwStartType, a malformed lpBinaryPathName or a bad account name as it does for correct
        /// ones. It cannot discriminate any of them. Those values are only exercised by an
        /// elevated end-to-end install.</para>
        /// </param>
        internal static int Create(
            string serviceName,
            string displayName,
            string binaryPath,
            string? accountName,
            IntPtr passwordPtr,
            uint scmAccess = DefaultScmAccess)
        {
            IntPtr scm = IntPtr.Zero;
            IntPtr svc = IntPtr.Zero;

            try
            {
                scm = OpenSCManagerW(null, null, scmAccess);
                if (scm == IntPtr.Zero)
                    return Marshal.GetLastWin32Error();

                svc = CreateServiceW(
                    scm,
                    serviceName,
                    displayName,
                    SERVICE_ALL_ACCESS,
                    SERVICE_WIN32_OWN_PROCESS,
                    SERVICE_AUTO_START,   // start= auto, as sc.exe was passed
                    SERVICE_ERROR_NORMAL,
                    binaryPath,
                    null,                 // no load-order group
                    IntPtr.Zero,          // no tag id
                    null,                 // no dependencies
                    accountName,          // null => LocalSystem, matching sc.exe's default
                    passwordPtr);

                if (svc == IntPtr.Zero)
                    return Marshal.GetLastWin32Error();

                return 0;
            }
            finally
            {
                if (svc != IntPtr.Zero) CloseServiceHandle(svc);
                if (scm != IntPtr.Zero) CloseServiceHandle(scm);
            }
        }

        /// <summary>
        /// The binPath the SCM stores for a service.
        ///
        /// THIS IS NOT THE STRING sc.exe WAS GIVEN, and the difference is the whole bug class the
        /// previous fix fought. Going through sc.exe the value crossed a command line, so the quotes
        /// around the exe path had to be BACKSLASH-ESCAPED (\"C:\...\SQLTriage.exe\" --service) to
        /// survive sc.exe's own CommandLineToArgvW pass. CreateServiceW takes the value as a
        /// parameter - there is no command line and no argv pass - so it must carry exactly ONE level
        /// of quoting. Copying the escaped form across would install a service whose stored binPath
        /// literally contained backslash-quote characters.
        ///
        /// One level is also what every real service on the box uses, e.g.
        ///     "C:\Program Files\OpenVPN Connect\agent_ovpnconnect.exe"
        /// The quotes still matter: without them the SCM's own argv pass splits "C:\Program Files\..."
        /// at the space and the service fails to start.
        /// </summary>
        internal static string BuildBinaryPath(string exePath) => $"\"{exePath}\" --service";

        /// <summary>
        /// Plain-English cause for the Win32 codes this install path actually returns. CreateServiceW
        /// reports through GetLastError the same codes sc.exe surfaces as its exit code, so both
        /// callers share one mapping and an operator sees the same sentence either way. Anything
        /// unmapped is reported as a bare code rather than guessed at.
        /// </summary>
        internal static string Describe(int code) => code switch
        {
            // ASCII only: this text goes to a redirected pipe and to consoles under a variety of
            // code pages. An em dash here came back as mush ("denied a??? run as") when captured.
            5    => "Access is denied - run as Administrator.",
            1057 => "The service account name is invalid, or the password is wrong for that account.",
            1060 => "No such service is installed.",
            1072 => "The service is marked for deletion - reboot, or close services.msc, then retry.",
            1073 => "A service with that name is already installed. Uninstall it first.",
            1078 => "That service name or display name is already in use.",
            _    => $"Windows reported error {code}."
        };
    }
}
