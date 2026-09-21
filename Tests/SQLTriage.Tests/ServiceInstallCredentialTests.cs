/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Runtime.InteropServices;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Covers the service-install credential slice: the command-line escaping that used to allow
    /// argument injection, the binPath translation from the sc.exe form to the CreateServiceW form,
    /// the accounts that do and do not need a password, and the rejection of --password.
    ///
    /// The escaping tests do NOT assert against a hand-written expectation of the escaped string.
    /// They hand the escaped command line to the REAL CommandLineToArgvW - the same parser Windows
    /// uses on a child process and the SCM uses on a binPath - and require the arguments to come
    /// back exactly as they went in. An escaping routine that is merely self-consistent would pass a
    /// hand-written expectation and still be wrong in production.
    /// </summary>
    public class ServiceInstallCredentialTests
    {
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(
            [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>
        /// Parses a command line with the real Windows parser. A leading program-name token is
        /// prepended because CommandLineToArgvW treats argv[0] under different rules than the rest,
        /// and it is the rest we are escaping.
        /// </summary>
        private static string[] ParseWithWindows(string commandLine)
        {
            var full = "prog.exe " + commandLine;
            IntPtr argv = CommandLineToArgvW(full, out int count);
            Assert.NotEqual(IntPtr.Zero, argv);
            try
            {
                var result = new string[count - 1];
                for (int i = 1; i < count; i++)
                {
                    IntPtr p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                    result[i - 1] = Marshal.PtrToStringUni(p) ?? "";
                }
                return result;
            }
            finally
            {
                LocalFree(argv);
            }
        }

        // ── Escaping: round trip through the real parser ───────────────────────────────

        [Theory]
        [InlineData("simple")]
        [InlineData("has space")]
        [InlineData("DOMAIN\\User")]
        [InlineData("C:\\Program Files\\SQLTriage\\SQLTriage.exe")]
        [InlineData("trailing\\backslash\\")]
        [InlineData("C:\\Program Files\\")]
        [InlineData("embedded\"quote")]
        [InlineData("\"fully quoted\"")]
        [InlineData("tab\there")]
        [InlineData("back\\\\slashes\\\\\"quote")]
        [InlineData("")]
        public void EscapeArgument_RoundTripsThroughTheRealWindowsParser(string argument)
        {
            var escaped = CommandLineEscaper.EscapeArgument(argument);
            var parsed = ParseWithWindows(escaped);

            Assert.Single(parsed);
            Assert.Equal(argument, parsed[0]);
        }

        [Fact]
        public void JoinArguments_RoundTripsAWholeInstallCommandLine()
        {
            var args = new[] { "--service", "--install", "--username", "DOMAIN\\svc account" };

            var parsed = ParseWithWindows(CommandLineEscaper.JoinArguments(args));

            Assert.Equal(args, parsed);
        }

        /// <summary>
        /// The injection the old quoting allowed, stated as a behaviour rather than as a shape.
        /// A username holding a double quote closed the quoted run early, and everything after it
        /// was re-parsed as command line syntax - so the value smuggled in extra arguments.
        /// </summary>
        [Fact]
        public void AUsernameContainingAQuote_CannotInjectExtraArguments()
        {
            var hostileUsername = "svc\" --uninstall --extra";
            var args = new[] { "--service", "--install", "--username", hostileUsername };

            var parsed = ParseWithWindows(CommandLineEscaper.JoinArguments(args));

            Assert.Equal(4, parsed.Length);
            Assert.Equal(hostileUsername, parsed[3]);
            Assert.DoesNotContain("--uninstall", parsed);
            Assert.DoesNotContain("--extra", parsed);
        }

        /// <summary>
        /// Pins that the test above actually discriminates. The superseded quoting rule is applied
        /// here to the same hostile value and must be shown to produce the injection - otherwise
        /// the test above could pass against any implementation and would be proving nothing.
        /// </summary>
        [Fact]
        public void TheSupersededQuotingRule_IsShownToAllowTheInjection()
        {
            var hostileUsername = "svc\" --uninstall --extra";
            var args = new[] { "--service", "--install", "--username", hostileUsername };

            // The old rule, verbatim: quote only when the value contains a space.
            var oldStyle = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            var parsed = ParseWithWindows(oldStyle);

            Assert.Contains("--uninstall", parsed);
            Assert.Contains("--extra", parsed);
            Assert.DoesNotContain(hostileUsername, parsed);
        }

        [Fact]
        public void TheSupersededQuotingRule_IsShownToSplitOnATab()
        {
            var value = "svc\taccount";

            var oldStyle = value.Contains(' ') ? $"\"{value}\"" : value;

            Assert.Equal(2, ParseWithWindows(oldStyle).Length);
            Assert.Single(ParseWithWindows(CommandLineEscaper.EscapeArgument(value)));
        }

        // ── binPath: the sc.exe form must NOT survive into CreateServiceW ──────────────

        /// <summary>
        /// The SCM parses a stored binPath with CommandLineToArgvW when it starts the service, so the
        /// path must come back as one token. This is the failure the previous sc.exe fix chased:
        /// C:\Program Files split at the space and the SCM received C:\Program.
        /// </summary>
        [Fact]
        public void BuildBinaryPath_KeepsAPathWithSpacesAsOneArgvToken()
        {
            const string exe = @"C:\Program Files\SQLTriage\SQLTriage.exe";

            var parsed = ParseWithWindows(Win32ServiceInstaller.BuildBinaryPath(exe));

            Assert.Equal(2, parsed.Length);
            Assert.Equal(exe, parsed[0]);
            Assert.Equal("--service", parsed[1]);
        }

        /// <summary>
        /// The regression that the translation to CreateServiceW invites: carrying the sc.exe
        /// backslash-escaped quoting across. CreateServiceW takes the value as a parameter, not on a
        /// command line, so a \" here would be stored in the SCM literally.
        /// </summary>
        [Fact]
        public void BuildBinaryPath_DoesNotCarryTheScExeBackslashEscapedQuoting()
        {
            var binPath = Win32ServiceInstaller.BuildBinaryPath(@"C:\Program Files\SQLTriage\SQLTriage.exe");

            Assert.DoesNotContain("\\\"", binPath);
            Assert.StartsWith("\"C:\\Program Files\\", binPath);
            Assert.EndsWith("\" --service", binPath);
        }

        // ── Which accounts need a password ─────────────────────────────────────────────

        [Theory]
        [InlineData("LocalSystem")]
        [InlineData("localsystem")]
        [InlineData("NT AUTHORITY\\SYSTEM")]
        [InlineData("NT AUTHORITY\\NetworkService")]
        [InlineData("nt authority\\networkservice")]
        [InlineData("NT AUTHORITY\\LocalService")]
        public void BuiltInServiceAccounts_AreNotPromptedForAPassword(string account)
            => Assert.False(WindowsServiceHost.AccountRequiresPassword(account));

        /// <summary>
        /// Group managed service accounts and computer accounts end in '$' and have no password a
        /// human can type - Windows rotates it. Prompting for one would block an install that is
        /// meant to be unattended.
        /// </summary>
        [Theory]
        [InlineData("DOMAIN\\gmsaSqlTriage$")]
        [InlineData("CONTOSO\\WEBSRV01$")]
        public void ManagedServiceAccounts_AreNotPromptedForAPassword(string account)
            => Assert.False(WindowsServiceHost.AccountRequiresPassword(account));

        [Theory]
        [InlineData("DOMAIN\\svc_sqltriage")]
        [InlineData(".\\localadmin")]
        [InlineData("svc_sqltriage")]
        public void AnOrdinaryAccount_IsPromptedForAPassword(string account)
            => Assert.True(WindowsServiceHost.AccountRequiresPassword(account));

        [Fact]
        public void AnEmptyAccount_IsNotPromptedForAPassword()
            => Assert.False(WindowsServiceHost.AccountRequiresPassword("   "));

        // ── --password is rejected, not ignored ───────────────────────────────────────

        /// <summary>
        /// Calls the REAL shipped InstallService. It must reject before touching the SCM, so this
        /// test can never install anything - the assertion that it returned the rejection code and
        /// printed the rejection sentence is what proves the early exit.
        /// </summary>
        [Fact]
        public void InstallService_RejectsThePasswordSwitch_AndNeverEchoesTheValue()
        {
            const string secret = "Sup3rS3cr3t!Pw";
            var original = Console.Error;
            var captured = new StringWriter();
            Console.SetError(captured);
            try
            {
                int exit = WindowsServiceHost.InstallService(
                    new[] { "--service", "--install", "--username", "DOMAIN\\svc", "--password", secret });

                Assert.Equal(1, exit);

                var text = captured.ToString();
                Assert.Contains("--password is not accepted", text);
                Assert.DoesNotContain(secret, text);
            }
            finally
            {
                Console.SetError(original);
            }
        }

        [Fact]
        public void ThePasswordRejectionMessage_PointsAtTheMechanismThatReplacedIt()
        {
            Assert.Contains("--username", WindowsServiceHost.PasswordSwitchRejected);
            Assert.Contains("--prompt-credentials", WindowsServiceHost.PasswordSwitchRejected);
            Assert.Contains("credential prompt", WindowsServiceHost.PasswordSwitchRejected);
        }

        // ── The prompt is opt-in, so an unattended caller cannot be hung by it ────────

        /// <summary>
        /// Found by mutation, not by reading. Environment.UserInteractive is true for a test runner
        /// and a CI agent exactly as it is for an operator at a desk, so an install that reached the
        /// credential prompt without being asked to would raise a modal dialog with nobody to answer
        /// it. A mutant that let --password through to that path hung this very suite on a live
        /// "Windows Security" dialog until the process was killed.
        ///
        /// This test IS the no-hang guarantee. If the guard is removed the call does not return a
        /// wrong answer, it blocks - so the wait is bounded here explicitly rather than through
        /// xUnit's Timeout property, whose applicability to a synchronous test this slice did not
        /// verify. A blocked call is reported as a failure, not as a hung suite.
        /// </summary>
        [Fact]
        public async Task AnAccountNeedingAPassword_IsRefusedRatherThanPromptedWhenNotOptedIn()
        {
            var original = Console.Error;
            var captured = new StringWriter();
            Console.SetError(captured);
            try
            {
                int exit = -1;
                var call = Task.Run(() => exit = WindowsServiceHost.InstallService(
                    new[] { "--service", "--install", "--username", "DOMAIN\\svc_sqltriage" }));

                var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(20)));

                Assert.True(finished == call,
                    "InstallService blocked instead of refusing - it reached the interactive "
                    + "credential prompt with no operator to answer it.");
                await call;

                Assert.Equal(1, exit);
                Assert.Contains("--prompt-credentials", captured.ToString());
            }
            finally
            {
                Console.SetError(original);
            }
        }

        [Fact]
        public void ThePromptCredentialsRequiredMessage_OffersTheUnattendedAlternatives()
        {
            Assert.Contains("--prompt-credentials", WindowsServiceHost.PromptCredentialsRequired);
            Assert.Contains("LocalSystem", WindowsServiceHost.PromptCredentialsRequired);
            Assert.Contains("managed service account", WindowsServiceHost.PromptCredentialsRequired);
        }

        // ── Error mapping ─────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(5, "Access is denied")]
        [InlineData(1057, "account name is invalid")]
        [InlineData(1060, "No such service")]
        [InlineData(1072, "marked for deletion")]
        [InlineData(1073, "already installed")]
        [InlineData(1078, "already in use")]
        public void Describe_GivesAPlainEnglishCause(int code, string expected)
            => Assert.Contains(expected, Win32ServiceInstaller.Describe(code));

        [Fact]
        public void Describe_ReportsAnUnmappedCodeRatherThanGuessing()
        {
            var text = Win32ServiceInstaller.Describe(4242);

            Assert.Contains("4242", text);
            Assert.DoesNotContain("Administrator", text);
        }

        /// <summary>
        /// The messages go to a redirected pipe and to consoles under a variety of code pages, where
        /// a non-ASCII character came back as mush ("denied a??? run as").
        /// </summary>
        [Theory]
        [InlineData(5)]
        [InlineData(1057)]
        [InlineData(1060)]
        [InlineData(1072)]
        [InlineData(1073)]
        [InlineData(1078)]
        public void Describe_StaysAscii(int code)
            => Assert.All(Win32ServiceInstaller.Describe(code), c => Assert.True(c < 128, $"non-ASCII in message for {code}"));

        // ── The P/Invoke itself, exercised against the real API ───────────────────────

        /// <summary>
        /// Marshalling that is never called proves nothing, so this drives the SHIPPED CreateServiceW
        /// declaration against real Windows. The SCM handle is opened with SC_MANAGER_CONNECT only:
        /// grantable without elevation, and carrying no create right, so Windows rejects the call
        /// with ERROR_ACCESS_DENIED whether or not this process is elevated - it can never create a
        /// service. What it does prove is that all thirteen parameters marshal and that the API
        /// returns a meaningful Win32 code rather than crashing on a malformed stack.
        /// </summary>
        [Fact]
        public void CreateServiceW_MarshalsAndReturnsARealWin32Error()
        {
            int error = Win32ServiceInstaller.Create(
                serviceName: "SQLTriageMarshallingProbe",
                displayName: "SQLTriage Marshalling Probe",
                binaryPath: Win32ServiceInstaller.BuildBinaryPath(@"C:\Program Files\SQLTriage\SQLTriage.exe"),
                accountName: null,
                passwordPtr: IntPtr.Zero,
                scmAccess: Win32ServiceInstaller.SC_MANAGER_CONNECT);

            Assert.Equal(5, error);
            Assert.Contains("Access is denied", Win32ServiceInstaller.Describe(error));
        }

        /// <summary>
        /// And the probe above is only meaningful if no such service exists to begin with.
        /// </summary>
        [Fact]
        public void TheMarshallingProbe_DoesNotLeaveAServiceBehind()
        {
            var existing = System.ServiceProcess.ServiceController.GetServices()
                .Any(s => s.ServiceName.Equals("SQLTriageMarshallingProbe", StringComparison.OrdinalIgnoreCase));

            Assert.False(existing);
        }

        /// <summary>
        /// The zeroing Dispose performs, observed on a live buffer this test still owns. Once
        /// Dispose has freed the memory its contents are undefined and cannot be read, so asserting
        /// through Dispose could only ever check that the pointer was nulled - a test that would
        /// carry "zeroes the password" in its name and prove nothing.
        /// </summary>
        [Fact]
        public void SecureZero_OverwritesThePlaintextItWasGiven()
        {
            const string password = "Sup3rS3cr3t!Pw";
            int bytes = (password.Length + 1) * 2;
            IntPtr buffer = Marshal.StringToCoTaskMemUni(password);

            try
            {
                // Precondition: the secret really is in there, so the assertion below is a change.
                Assert.Equal(password, Marshal.PtrToStringUni(buffer));
                var before = new byte[bytes];
                Marshal.Copy(buffer, before, 0, bytes);
                Assert.Contains(before, b => b != 0);

                PromptedServiceCredential.SecureZero(buffer, bytes);

                var after = new byte[bytes];
                Marshal.Copy(buffer, after, 0, bytes);
                Assert.All(after, b => Assert.Equal(0, b));
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        /// <summary>
        /// Dispose must hand the buffer to SecureZero and then release the pointer, and must be safe
        /// to call twice - a double free on unmanaged memory corrupts the heap.
        /// </summary>
        [Fact]
        public void PromptedServiceCredential_Dispose_ReleasesThePointerAndIsIdempotent()
        {
            IntPtr buffer = Marshal.StringToCoTaskMemUni("Sup3rS3cr3t!Pw");
            var credential = new PromptedServiceCredential("DOMAIN\\svc", buffer, 30);

            Assert.Equal(buffer, credential.PasswordPtr);

            credential.Dispose();
            Assert.Equal(IntPtr.Zero, credential.PasswordPtr);

            credential.Dispose();
            Assert.Equal(IntPtr.Zero, credential.PasswordPtr);
        }
    }
}
