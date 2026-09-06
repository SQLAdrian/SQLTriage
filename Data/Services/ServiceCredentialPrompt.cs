/* In the name of God, the Merciful, the Compassionate */

using System.Runtime.InteropServices;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Collects a service-account password from the operator using the Windows credential dialog,
    /// inside the already-elevated install process.
    ///
    /// WHY A DIALOG AND NOT STDIN OR AN ENVIRONMENT VARIABLE. The installer is reached by two
    /// different process-start paths and only a dialog works on both. Measured on this machine
    /// (.NET 10, Windows 11) rather than assumed:
    ///   * ProcessStartInfo.UseShellExecute=true + RedirectStandardInput=true throws
    ///     InvalidOperationException "The Process object must have the UseShellExecute property
    ///     set to false in order to redirect IO streams."
    ///   * UseShellExecute=true + Environment[...] throws InvalidOperationException "...in order to
    ///     use environment variables."
    ///   * Verb="runas" with UseShellExecute=false does NOT throw — it is silently ignored and the
    ///     child starts UNELEVATED. So the UAC path cannot simply switch to UseShellExecute=false
    ///     to regain the streams; it would stop elevating and the install would fail.
    /// A GUI dialog needs neither a stream nor an environment block, so it is the one mechanism
    /// available on both paths.
    ///
    /// THE PASSWORD IS NEVER A MANAGED STRING. CredUnPackAuthenticationBuffer writes it into
    /// unmanaged memory we own, and that pointer is handed straight to CreateServiceW's lpPassword.
    /// A System.String would be immutable, GC-relocatable and unzeroable — it could sit in the heap
    /// (and any crash dump of it) until collected. Dispose zeroes the buffer before freeing.
    /// </summary>
    internal sealed class PromptedServiceCredential : IDisposable
    {
        /// <summary>The account the operator confirmed in the elevated dialog. Not a secret.</summary>
        public string UserName { get; }

        /// <summary>
        /// Unmanaged, null-terminated UTF-16 password buffer. Valid until Dispose. Passed directly
        /// to CreateServiceW; deliberately never copied into a managed string.
        /// </summary>
        public IntPtr PasswordPtr { get; private set; }

        private readonly int _passwordBytes;
        private bool _disposed;

        internal PromptedServiceCredential(string userName, IntPtr passwordPtr, int passwordBytes)
        {
            UserName = userName;
            PasswordPtr = passwordPtr;
            _passwordBytes = passwordBytes;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (PasswordPtr != IntPtr.Zero)
            {
                SecureZero(PasswordPtr, _passwordBytes);
                Marshal.FreeCoTaskMem(PasswordPtr);
                PasswordPtr = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Overwrites an unmanaged buffer with zeroes before it is returned to the allocator -
        /// freeing alone leaves the plaintext readable in reusable heap memory.
        ///
        /// Factored out of Dispose so it can be EXERCISED. Once the buffer is freed its contents are
        /// undefined and unreadable, so a test that only called Dispose could assert the pointer was
        /// nulled and nothing more - it would carry "zeroes the password" in its name while proving
        /// no such thing. Marshal.WriteByte in a loop rather than an unsafe block so this needs no
        /// AllowUnsafeBlocks.
        /// </summary>
        internal static void SecureZero(IntPtr buffer, int byteCount)
        {
            if (buffer == IntPtr.Zero) return;
            for (int i = 0; i < byteCount; i++)
                Marshal.WriteByte(buffer, i, 0);
        }
    }

    internal static class ServiceCredentialPrompt
    {
        internal const int ERROR_CANCELLED = 1223;

        private const int CREDUIWIN_GENERIC = 0x00000001;
        private const int CRED_MAX_USERNAME_LENGTH = 513;
        private const int CRED_MAX_PASSWORD_LENGTH = 256;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDUI_INFO
        {
            public int cbSize;
            public IntPtr hwndParent;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszMessageText;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszCaptionText;
            public IntPtr hbmBanner;
        }

        [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int CredUIPromptForWindowsCredentials(
            ref CREDUI_INFO pUiInfo,
            int dwAuthError,
            ref uint pulAuthPackage,
            IntPtr pvInAuthBuffer,
            uint ulInAuthBufferSize,
            out IntPtr ppvOutAuthBuffer,
            out uint pulOutAuthBufferSize,
            ref bool pfSave,
            int dwFlags);

        [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredPackAuthenticationBufferW(
            int dwFlags,
            [MarshalAs(UnmanagedType.LPWStr)] string pszUserName,
            [MarshalAs(UnmanagedType.LPWStr)] string pszPassword,
            IntPtr pPackedCredentials,
            ref uint pcbPackedCredentials);

        [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredUnPackAuthenticationBufferW(
            int dwFlags,
            IntPtr pAuthBuffer,
            uint cbAuthBuffer,
            IntPtr pszUserName,
            ref uint pcchMaxUserName,
            IntPtr pszDomainName,
            ref uint pcchMaxDomainName,
            IntPtr pszPassword,
            ref uint pcchMaxPassword);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr ptr);

        /// <summary>
        /// Shows the Windows credential dialog, pre-filled with <paramref name="suggestedUserName"/>.
        /// Returns null and sets <paramref name="error"/> when the operator cancels, or when there is
        /// no interactive desktop to draw on.
        /// </summary>
        internal static PromptedServiceCredential? Prompt(string suggestedUserName, out string error)
        {
            error = "";

            // A service host started with no interactive desktop (Session 0, or a headless --server
            // box) has nowhere to draw the dialog. Failing here with an instruction beats putting up
            // a window nobody can see and appearing to hang.
            if (!Environment.UserInteractive)
            {
                error = "No interactive desktop is available to prompt for the service account password. "
                      + "Run the install from an interactive session.";
                return null;
            }

            var info = new CREDUI_INFO
            {
                hwndParent = IntPtr.Zero,
                pszCaptionText = "SQLTriage Service Account",
                pszMessageText = $"Enter the password for the account that will run the {WindowsServiceHost.ServiceDisplayName} service.",
                hbmBanner = IntPtr.Zero
            };
            info.cbSize = Marshal.SizeOf(info);

            IntPtr inBuffer = IntPtr.Zero;
            uint inBufferSize = 0;
            IntPtr outBuffer = IntPtr.Zero;
            uint outBufferSize = 0;

            IntPtr userPtr = IntPtr.Zero;
            IntPtr domainPtr = IntPtr.Zero;
            IntPtr passwordPtr = IntPtr.Zero;

            try
            {
                // Pre-seed the account so the operator confirms the identity the UI showed rather
                // than retyping it. An empty password is packed alongside; it is only a seed.
                CredPackAuthenticationBufferW(0, suggestedUserName, string.Empty, IntPtr.Zero, ref inBufferSize);
                if (inBufferSize > 0)
                {
                    inBuffer = Marshal.AllocCoTaskMem((int)inBufferSize);
                    if (!CredPackAuthenticationBufferW(0, suggestedUserName, string.Empty, inBuffer, ref inBufferSize))
                    {
                        Marshal.FreeCoTaskMem(inBuffer);
                        inBuffer = IntPtr.Zero;
                        inBufferSize = 0;
                    }
                }

                uint authPackage = 0;
                bool save = false;

                int result = CredUIPromptForWindowsCredentials(
                    ref info,
                    0,
                    ref authPackage,
                    inBuffer,
                    inBufferSize,
                    out outBuffer,
                    out outBufferSize,
                    ref save,
                    CREDUIWIN_GENERIC);

                if (result == ERROR_CANCELLED)
                {
                    error = "Install cancelled - no password was entered.";
                    return null;
                }
                if (result != 0)
                {
                    error = $"The Windows credential prompt failed (error {result}).";
                    return null;
                }

                uint userLen = CRED_MAX_USERNAME_LENGTH;
                uint domainLen = CRED_MAX_USERNAME_LENGTH;
                uint passwordLen = CRED_MAX_PASSWORD_LENGTH;

                // Sizes are in CHARACTERS; the buffers are UTF-16, so two bytes each, plus a
                // null terminator's worth.
                userPtr = Marshal.AllocCoTaskMem((int)userLen * 2);
                domainPtr = Marshal.AllocCoTaskMem((int)domainLen * 2);
                passwordPtr = Marshal.AllocCoTaskMem((int)passwordLen * 2);

                if (!CredUnPackAuthenticationBufferW(
                        0, outBuffer, outBufferSize,
                        userPtr, ref userLen,
                        domainPtr, ref domainLen,
                        passwordPtr, ref passwordLen))
                {
                    error = $"Could not read the entered credential (error {Marshal.GetLastWin32Error()}).";
                    return null;
                }

                // The username is not a secret, so it may safely become a managed string.
                var user = Marshal.PtrToStringUni(userPtr) ?? suggestedUserName;

                // Hand ownership of the password buffer to the returned object. Deliberately NOT
                // read into a string here.
                var owned = passwordPtr;
                passwordPtr = IntPtr.Zero;
                return new PromptedServiceCredential(user, owned, (int)passwordLen * 2);
            }
            finally
            {
                if (inBuffer != IntPtr.Zero) Marshal.FreeCoTaskMem(inBuffer);
                if (userPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(userPtr);
                if (domainPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(domainPtr);

                // Only reached when we failed before handing ownership over.
                if (passwordPtr != IntPtr.Zero)
                {
                    PromptedServiceCredential.SecureZero(passwordPtr, CRED_MAX_PASSWORD_LENGTH * 2);
                    Marshal.FreeCoTaskMem(passwordPtr);
                }

                if (outBuffer != IntPtr.Zero)
                {
                    // The credential buffer CredUIPromptForWindowsCredentials handed back holds the
                    // plaintext as well, so zero it before returning it to the allocator.
                    PromptedServiceCredential.SecureZero(outBuffer, (int)outBufferSize);
                    CoTaskMemFree(outBuffer);
                }
            }
        }
    }
}
