/* In the name of God, the Merciful, the Compassionate */

using System.Threading;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    /// <summary>
    /// Emits the single "no servers configured — collectors idle" startup line, at most once per
    /// process. Background collectors and the host startup path all call <see cref="AnnounceOnce"/>
    /// when they observe the no-server-idle posture; the first caller logs, every later caller is a
    /// no-op. This keeps the idle notice to EXACTLY ONE line no matter how many loops observe it, and
    /// produces NO per-tick noise thereafter (the no-server-idle ruling).
    /// </summary>
    public static class NoServerIdleNotice
    {
        internal const string Message = "No servers configured - collectors idle until a server is added";

        private static int _announced;

        /// <summary>Logs the idle notice once. Subsequent calls do nothing.</summary>
        public static void AnnounceOnce(ILogger logger)
        {
            if (logger == null) return;
            if (Interlocked.Exchange(ref _announced, 1) == 0)
                logger.LogInformation(Message);
        }

        /// <summary>Test hook: clears the once-only latch so a test can assert the exactly-once behaviour.</summary>
        internal static void ResetForTests() => Interlocked.Exchange(ref _announced, 0);
    }
}
