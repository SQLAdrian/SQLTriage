/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Simple file-based logging service for debugging purposes.
    /// Logs are written to logs/SqlHealthAssessment.log with size-based rotation.
    /// </summary>
    public class LocalLogService : IDisposable
    {
        private readonly string _logDirectory;
        private readonly string _logFilePath;
        private readonly object _lock = new();
        private readonly long _maxFileSizeBytes;
        private StreamWriter? _writer;
        private bool _disposed;

        public LocalLogService(long maxFileSizeBytes = 5 * 1024 * 1024) // 5MB default
        {
            _maxFileSizeBytes = maxFileSizeBytes;
            _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(_logDirectory);
            
            // Single consolidated log file
            var logFileName = "SqlHealthAssessment.log";
            _logFilePath = Path.Combine(_logDirectory, logFileName);
            
            InitializeWriter();
        }

        private void InitializeWriter()
        {
            // Check if we need to rotate files
            if (File.Exists(_logFilePath))
            {
                var fileInfo = new FileInfo(_logFilePath);
                if (fileInfo.Length >= _maxFileSizeBytes)
                {
                    RotateLogFile();
                }
            }

            _writer = new StreamWriter(_logFilePath, append: true, encoding: Encoding.UTF8)
            {
                AutoFlush = true
            };
        }

        private void RotateLogFile()
        {
            _writer?.Dispose();
            
            var timestamp = DateTime.Now.ToString("HHmmss");
            var rotatedName = Path.GetFileNameWithoutExtension(_logFilePath) + $"-{timestamp}.log";
            var rotatedPath = Path.Combine(_logDirectory, rotatedName);
            
            if (File.Exists(_logFilePath))
            {
                File.Move(_logFilePath, rotatedPath);
            }
            
            InitializeWriter();
        }

        public void Log(LogLevel level, string message, Exception? exception = null)
        {
            if (_disposed) return;

            lock (_lock)
            {
                try
                {
                    // Check file size before writing
                    if (File.Exists(_logFilePath))
                    {
                        var fileInfo = new FileInfo(_logFilePath);
                        if (fileInfo.Length >= _maxFileSizeBytes)
                        {
                            RotateLogFile();
                        }
                    }

                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var logEntry = new StringBuilder();
                    logEntry.AppendLine($"[{timestamp}] [{level}] {message}");
                    
                    if (exception != null)
                    {
                        logEntry.AppendLine($"  Exception: {exception.GetType().Name}: {exception.Message}");
                        logEntry.AppendLine($"  StackTrace: {exception.StackTrace}");
                        
                        if (exception.InnerException != null)
                        {
                            logEntry.AppendLine($"  InnerException: {exception.InnerException.Message}");
                        }
                    }

                    _writer?.Write(logEntry.ToString());
                }
                catch
                {
                    // Silently fail to prevent logging from causing app crashes
                }
            }
        }

        public void LogInfo(string message) => Log(LogLevel.Info, message);
        public void LogWarning(string message) => Log(LogLevel.Warning, message);
        public void LogError(string message, Exception? ex = null) => Log(LogLevel.Error, message, ex);
        public void LogDebug(string message) => Log(LogLevel.Debug, message);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            lock (_lock)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }
}
