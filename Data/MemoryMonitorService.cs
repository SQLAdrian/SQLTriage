/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    public class MemoryMonitorService : IDisposable
    {
        private readonly ILogger<MemoryMonitorService> _logger;
        private readonly System.Timers.Timer _timer;
        private readonly long _thresholdBytes;
        private bool _isUnderPressure;

        public event EventHandler<MemoryPressureEventArgs>? MemoryPressureChanged;

        public bool IsUnderPressure => _isUnderPressure;
        public long CurrentMemoryUsage { get; private set; }
        public double MemoryUsagePercent { get; private set; }

        public MemoryMonitorService(ILogger<MemoryMonitorService> logger, double thresholdPercent = 0.8)
        {
            _logger = logger;
            _thresholdBytes = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * thresholdPercent);
            _timer = new System.Timers.Timer(5000); // Check every 5 seconds
            _timer.Elapsed += CheckMemoryPressure;
            _timer.Start();
        }

        private void CheckMemoryPressure(object? sender, System.Timers.ElapsedEventArgs e)
        {
            var process = Process.GetCurrentProcess();
            CurrentMemoryUsage = process.WorkingSet64;
            var totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            MemoryUsagePercent = (double)CurrentMemoryUsage / totalMemory;

            var wasUnderPressure = _isUnderPressure;
            _isUnderPressure = CurrentMemoryUsage > _thresholdBytes;

            if (_isUnderPressure != wasUnderPressure)
            {
                _logger.LogWarning("Memory pressure changed: {Status}. Usage: {Usage:N0} MB ({Percent:P1})",
                    _isUnderPressure ? "HIGH" : "NORMAL",
                    CurrentMemoryUsage / 1024.0 / 1024.0,
                    MemoryUsagePercent);

                MemoryPressureChanged?.Invoke(this, new MemoryPressureEventArgs(_isUnderPressure, CurrentMemoryUsage, MemoryUsagePercent));
            }
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
        }
    }

    public class MemoryPressureEventArgs : EventArgs
    {
        public bool IsUnderPressure { get; }
        public long MemoryUsage { get; }
        public double MemoryPercent { get; }

        public MemoryPressureEventArgs(bool isUnderPressure, long memoryUsage, double memoryPercent)
        {
            IsUnderPressure = isUnderPressure;
            MemoryUsage = memoryUsage;
            MemoryPercent = memoryPercent;
        }
    }
}
