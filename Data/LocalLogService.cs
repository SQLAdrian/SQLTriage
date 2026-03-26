/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Thin wrapper over ILogger so Blazor components can inject a named logger
    /// without coupling to Microsoft.Extensions.Logging directly.
    /// All output is routed through Serilog (configured in App.xaml.cs).
    /// </summary>
    public class LocalLogService
    {
        private readonly ILogger<LocalLogService> _logger;

        public LocalLogService(ILogger<LocalLogService> logger)
        {
            _logger = logger;
        }

        public void LogInfo(string message)    => _logger.LogInformation("{Message}", message);
        public void LogWarning(string message) => _logger.LogWarning("{Message}", message);
        public void LogDebug(string message)   => _logger.LogDebug("{Message}", message);
        public void LogError(string message, Exception? ex = null)
        {
            if (ex != null)
                _logger.LogError(ex, "{Message}", message);
            else
                _logger.LogError("{Message}", message);
        }
    }

    // Kept for any remaining references — maps to Microsoft.Extensions.Logging levels
    public enum LogLevel { Debug, Info, Warning, Error }
}
