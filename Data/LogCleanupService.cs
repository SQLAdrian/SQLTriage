/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    /// <summary>
    /// Deletes service log files older than 7 days from .\logs. Runs once at startup then every
    /// 24 hours.
    ///
    /// <para>THE BOUNDARY: .\audit-logs is NEVER swept by this service, and no age-based rule may
    /// be added for it. That directory belongs to <see cref="AuditLogService"/>, which deletes
    /// only files matching <c>audit-*.jsonl</c>, keyed off the UTC date embedded in the filename,
    /// on its own retention (Audit:RetentionDays, default 90). That sweep cannot match key
    /// material, so it is safe where a blanket one is not.</para>
    ///
    /// <para>WHY, dated: on 2026-08-11, at the first start of build 3511, this service swept
    /// .\audit-logs by last-write time and deleted the audit signing key <c>hmac.key</c> (mtime
    /// frozen at 2026-08-04), its archive <c>hmac.key.72D832D61F2D1E14</c>, and the 492 KB
    /// evidence segment <c>audit-2026-07-31.jsonl</c>. The audit service then honestly minted a
    /// fresh key by first-mint, and 469 chain entries became unverifiable. The files were restored
    /// from the pre-deploy backup.</para>
    ///
    /// <para>The mechanism is not a tuning problem, so a longer retention does not fix it: EVERY
    /// file in .\audit-logs ages past any cutoff by design. Segments roll, so a closed segment is
    /// never written again and its last-write time freezes on the day it closed. A key freezes at
    /// its last write and then stays needed for as long as the entries it signed are worth
    /// verifying. Key material has no age at which deletion is safe.</para>
    /// </summary>
    public class LogCleanupService : IDisposable
    {
        private readonly ILogger<LogCleanupService> _logger;
        private Timer? _timer;

        /// <summary>
        /// Swept directories. "audit-logs" was removed on 2026-08-11 (Adrian's ruling, same day as
        /// the incident in the class summary) and must not be restored.
        /// </summary>
        private static readonly string[] _directories = { "logs" };
        private const int RetentionDays = 7;

        // Test seam (InternalsVisibleTo SQLTriage.Tests): lets a test point the sweep at its own
        // temporary base instead of AppDomain.CurrentDomain.BaseDirectory. Null in production, so
        // the public constructor below leaves the production path exactly as it was.
        private readonly string? _baseDirOverride;

        public LogCleanupService(ILogger<LogCleanupService> logger)
            : this(logger, null) { }

        internal LogCleanupService(ILogger<LogCleanupService> logger, string? baseDirOverride)
        {
            _logger = logger;
            _baseDirOverride = baseDirOverride;
        }

        /// <summary>
        /// The directory the sweep runs under. This is the single expression <see cref="Cleanup"/>
        /// reads, so a test asserting it for the public constructor is asserting the production
        /// path itself and not a copy of it.
        /// </summary>
        internal string EffectiveBaseDirectory =>
            _baseDirOverride ?? AppDomain.CurrentDomain.BaseDirectory;

        public void Start()
        {
            // Run immediately at startup, then every 24 hours
            _timer = new Timer(_ => Cleanup(), null,
                dueTime: TimeSpan.Zero,
                period: TimeSpan.FromHours(24));
        }

        // Internal (not private) for test visibility (InternalsVisibleTo SQLTriage.Tests): the
        // regression test drives this method rather than waiting on the 24-hour timer.
        internal void Cleanup()
        {
            var baseDir = EffectiveBaseDirectory;
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            var deleted = 0;

            foreach (var dirName in _directories)
            {
                var dirPath = Path.Combine(baseDir, dirName);
                if (!Directory.Exists(dirPath)) continue;

                foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                        {
                            File.Delete(file);
                            deleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "LogCleanup: could not delete {File}", file);
                    }
                }
            }

            if (deleted > 0)
                _logger.LogInformation("LogCleanup: deleted {Count} file(s) older than {Days} days", deleted, RetentionDays);
        }

        public void Dispose() => _timer?.Dispose();
    }
}
