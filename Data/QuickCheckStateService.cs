/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data
{
    /// <summary>
    /// Singleton state container for the Quick Check page.
    /// Persists run results, progress, and diagnostic log across navigation.
    /// Also owns the CheckExecutor.OnCheckCompleted subscription so diagnostic
    /// entries continue to be captured even when the page is not mounted.
    /// DiagLog is capped to prevent unbounded memory growth.
    /// </summary>
    public class QuickCheckStateService : IDisposable
    {
        private readonly CheckExecutionService _checkExecutor;

        /// <summary>Maximum diagnostic log entries retained.</summary>
        private const int MaxDiagLogEntries = 2000;

        // ── Run state ────────────────────────────────────────────────────────
        public List<CheckResult> Results { get; set; } = new();
        public List<CheckExecutionSummary> ServerSummaries { get; set; } = new();
        public List<string> Categories { get; set; } = new();
        public List<DiagLogEntry> DiagLog { get; set; } = new();

        public bool IsRunning { get; set; }
        public bool IsImporting { get; set; }
        public bool HasRun { get; set; }
        public int Progress { get; set; }
        public string ProgressMessage { get; set; } = string.Empty;
        public string StatusMessage { get; set; } = string.Empty;
        public string StatusClass { get; set; } = string.Empty;
        public DateTime ExecutionTime { get; set; }
        public string ExecutionDuration { get; set; } = string.Empty;
        public int ServersTested { get; set; }

        // ── UI preference state (survives navigation) ─────────────────────
        public bool RunAllServers { get; set; } = true;
        public string SelectedConnectionId { get; set; } = string.Empty;
        public string SelectedFilter { get; set; } = "all";
        public string SelectedCategory { get; set; } = string.Empty;
        public string SelectedServer { get; set; } = string.Empty;
        public string SelectedSeverity { get; set; } = string.Empty;

        // ── Change notification ───────────────────────────────────────────
        public event Action? StateChanged;
        public void NotifyStateChanged() => StateChanged?.Invoke();

        public QuickCheckStateService(CheckExecutionService checkExecutor)
        {
            _checkExecutor = checkExecutor;
            _checkExecutor.OnCheckCompleted += HandleCheckCompleted;
        }

        // Owned by the service so diagnostic logging continues after page navigation
        private void HandleCheckCompleted(CheckResult result)
        {
            var status = !string.IsNullOrEmpty(result.ErrorMessage) ? "ERR"
                       : result.Passed ? "PASS" : "FAIL";
            var level = !string.IsNullOrEmpty(result.ErrorMessage) ? "error"
                      : result.Passed ? "pass" : "fail";

            var msg = $"[{result.InstanceName}] {status} {result.CheckId} \"{result.CheckName}\" " +
                      $"(val={result.ActualValue}, exp={result.ExpectedValue}, {result.DurationMs}ms)" +
                      (!string.IsNullOrEmpty(result.ErrorMessage) ? $" - {result.ErrorMessage}" : "");

            AppendDiagEntry(msg, level);
        }

        public void AddDiagEntry(string message, string level = "info")
        {
            AppendDiagEntry(message, level);
        }

        private void AppendDiagEntry(string message, string level)
        {
            DiagLog.Add(new DiagLogEntry { Timestamp = DateTime.Now, Message = message, Level = level });

            // Evict oldest entries to prevent unbounded growth
            if (DiagLog.Count > MaxDiagLogEntries)
                DiagLog.RemoveRange(0, DiagLog.Count - MaxDiagLogEntries);

            NotifyStateChanged();
        }

        public void ClearDiagLog()
        {
            DiagLog.Clear();
            NotifyStateChanged();
        }

        public void ClearResults()
        {
            _checkExecutor.ClearAllResults();
            Results.Clear();
            ServerSummaries.Clear();
            HasRun = false;
            StatusMessage = string.Empty;
            SelectedFilter = "all";
            SelectedCategory = string.Empty;
            SelectedServer = string.Empty;
            SelectedSeverity = string.Empty;
            NotifyStateChanged();
        }

        public void Dispose() => _checkExecutor.OnCheckCompleted -= HandleCheckCompleted;

        public class DiagLogEntry
        {
            public DateTime Timestamp { get; set; }
            public string Message { get; set; } = string.Empty;
            public string Level { get; set; } = "info";
        }
    }
}
