/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading;

namespace SQLTriage.Data
{
    /// <summary>
    /// Helpers for Microsoft Entra MFA (Azure AD Interactive) authentication.
    /// Provides cancellation detection and a serialisation lock so that
    /// only one MFA browser popup appears at a time.
    /// </summary>
    public static class MfaAuthenticationHelper
    {
        /// <summary>
        /// Serialises MFA authentication attempts so multiple connections
        /// don't each open their own browser popup simultaneously.
        /// </summary>
        public static readonly SemaphoreSlim MfaAuthLock = new(1, 1);

        /// <summary>
        /// Returns true when the exception indicates the user cancelled
        /// the MFA browser popup rather than completing authentication.
        /// </summary>
        public static bool IsMfaCancelledException(Exception ex)
        {
            var message = ex.Message?.ToLowerInvariant() ?? string.Empty;

            return message.Contains("user canceled")
                || message.Contains("user cancelled")
                || message.Contains("authentication was cancelled")
                || message.Contains("authentication was canceled");
        }
    }
}
