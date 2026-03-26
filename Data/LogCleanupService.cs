/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Deletes log files older than 7 days from .\logs and .\audit-logs.
    /// Runs once at startup then every 24 hours.
    /// </summary>
    public class LogCleanupService : IDisposable
    {
        private readonly ILogger<LogCleanupService> _logger;
        private Timer? _timer;

        private static readonly string[] _directories = { "logs", "audit-logs" };
        private const int RetentionDays = 7;

        public LogCleanupService(ILogger<LogCleanupService> logger)
        {
            _logger = logger;
        }

        public void Start()
        {
            // Run immediately at startup, then every 24 hours
            _timer = new Timer(_ => Cleanup(), null,
                dueTime: TimeSpan.Zero,
                period: TimeSpan.FromHours(24));
        }

        private void Cleanup()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var cutoff  = DateTime.Now.AddDays(-RetentionDays);
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
