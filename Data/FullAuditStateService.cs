/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Stores state for the Full Audit page to persist across navigation.
    /// Results are automatically cleared at the start of each new execution
    /// to prevent unbounded memory growth across repeated audit runs.
    /// </summary>
    public class FullAuditStateService
    {
        private string? _selectedConnectionId;
        private string? _selectedServer;
        private readonly ConcurrentDictionary<string, ScriptExecutionResult> _executionResults = new();
        private bool _isRunning;
        private bool _isTesting;
        private readonly object _lock = new();
        private DateTime _lastClearTime = DateTime.Now;

        /// <summary>Maximum results retained. Oldest entries are evicted when exceeded.</summary>
        private const int MaxResults = 500;
        
        // Progress tracking state
        private int _totalServers;
        private int _currentServerIndex;
        private string _currentServerName = "";
        private int _totalScripts;
        private int _currentScriptIndex;
        private string _currentScriptName = "";
        private DateTime _executionStartTime;
        private bool _isMultiServerExecution;

        public string? SelectedConnectionId
        {
            get => _selectedConnectionId;
            set => _selectedConnectionId = value;
        }

        public string? SelectedServer
        {
            get => _selectedServer;
            set => _selectedServer = value;
        }

        public bool IsRunning
        {
            get => _isRunning;
            set => _isRunning = value;
        }

        public bool IsTesting
        {
            get => _isTesting;
            set => _isTesting = value;
        }

        // Progress tracking properties
        public int TotalServers
        {
            get => _totalServers;
            set => _totalServers = value;
        }

        public int CurrentServerIndex
        {
            get => _currentServerIndex;
            set => _currentServerIndex = value;
        }

        public string CurrentServerName
        {
            get => _currentServerName;
            set => _currentServerName = value ?? "";
        }

        public int TotalScripts
        {
            get => _totalScripts;
            set => _totalScripts = value;
        }

        public int CurrentScriptIndex
        {
            get => _currentScriptIndex;
            set => _currentScriptIndex = value;
        }

        public string CurrentScriptName
        {
            get => _currentScriptName;
            set => _currentScriptName = value ?? "";
        }

        public DateTime ExecutionStartTime
        {
            get => _executionStartTime;
            set => _executionStartTime = value;
        }

        public bool IsMultiServerExecution
        {
            get => _isMultiServerExecution;
            set => _isMultiServerExecution = value;
        }

        public double ProgressPercent
        {
            get
            {
                if (_totalServers * _totalScripts <= 0) return 0;
                // Indices are 1-based: serverIndex 1..N, scriptIndex 0..M (0 = starting server, M = all scripts done)
                var completed = (_currentServerIndex - 1) * _totalScripts + _currentScriptIndex;
                var total = _totalServers * _totalScripts;
                var pct = (double)completed / total * 100;
                return Math.Min(pct, 100);
            }
        }

        public void AddExecutionResult(ScriptExecutionResult result)
        {
            // Clear large result data to save memory - we only need metadata
            if (result.Results != null && result.Results.Count > 0)
            {
                // Keep only summary info, clear the actual data to free memory
                result.RowsAffected = result.Results.Count;
                result.Results = null; // Free the memory
            }
            _executionResults[result.ScriptName] = result;

            // Evict oldest entries if dictionary exceeds cap
            if (_executionResults.Count > MaxResults)
            {
                var oldest = _executionResults
                    .OrderBy(kv => kv.Value.ExecutionTime)
                    .Take(_executionResults.Count - MaxResults)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in oldest)
                    _executionResults.TryRemove(key, out _);
            }
        }

        public void RemoveExecutionResult(string scriptName)
        {
            _executionResults.TryRemove(scriptName, out _);
        }

        public void ClearExecutionResults()
        {
            _executionResults.Clear();
            _lastClearTime = DateTime.Now;
            ResetProgress();
            // GC will reclaim memory naturally - no forced collection needed
        }

        public void ResetProgress()
        {
            _totalServers = 0;
            _currentServerIndex = 0;
            _currentServerName = "";
            _totalScripts = 0;
            _currentScriptIndex = 0;
            _currentScriptName = "";
            _executionStartTime = DateTime.MinValue;
            _isMultiServerExecution = false;
        }

        public void StartExecution(int totalServers, int totalScripts, bool isMultiServer = false)
        {
            // Clear previous run results to prevent unbounded memory growth
            _executionResults.Clear();

            _totalServers = totalServers;
            _totalScripts = totalScripts;
            _currentServerIndex = 0;
            _currentScriptIndex = 0;
            _executionStartTime = DateTime.Now;
            _isMultiServerExecution = isMultiServer;
            _isRunning = true;
        }

        public void UpdateProgress(int serverIndex, string serverName, int scriptIndex, string scriptName)
        {
            _currentServerIndex = serverIndex;
            _currentServerName = serverName;
            _currentScriptIndex = scriptIndex;
            _currentScriptName = scriptName;
        }

        public void ClearAndAddResults(IEnumerable<ScriptExecutionResult> results)
        {
            _executionResults.Clear();
            foreach (var result in results)
            {
                AddExecutionResult(result);
            }
            _lastClearTime = DateTime.Now;
        }

        public ScriptExecutionResult? GetExecutionResult(string scriptName)
        {
            return _executionResults.TryGetValue(scriptName, out var result) ? result : null;
        }

        public List<ScriptExecutionResult> GetAllExecutionResults()
        {
            return _executionResults.Values.ToList();
        }

        public bool HasExecutionResults => !_executionResults.IsEmpty;

        public int ResultCount => _executionResults.Count;

        /// <summary>
        /// Clears all execution results and releases references so the GC can reclaim memory naturally.
        /// Forced GC.Collect was removed as it causes UI freezes and promotes objects to Gen2 prematurely.
        /// </summary>
        public void ForceCleanup()
        {
            ClearExecutionResults();
            _lastClearTime = DateTime.Now;
        }

        public TimeSpan ElapsedTime => _executionStartTime != DateTime.MinValue 
            ? DateTime.Now - _executionStartTime 
            : TimeSpan.Zero;
    }
}
