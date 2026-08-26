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
        // Nullable DI seam (repo convention): registered in the app, omitted in unit
        // tests so the debounce can be exercised without CheckExecutionService's graph.
        private readonly CheckExecutionService? _checkExecutor;

        /// <summary>Maximum diagnostic log entries retained.</summary>
        private const int MaxDiagLogEntries = 2000;
        private readonly object _diagLogLock = new();
        private List<DiagLogEntry> _diagLog = new();

        // ── Run state ────────────────────────────────────────────────────────
        public List<CheckResult> Results { get; set; } = new();
        public List<CheckExecutionSummary> ServerSummaries { get; set; } = new();
        public List<string> Categories { get; set; } = new();
        public IReadOnlyList<DiagLogEntry> DiagLog 
        { 
            get 
            {
                lock (_diagLogLock) { return _diagLog.ToList(); }
            }
        }

        public bool IsRunning { get; set; }
        public bool HasRun { get; set; }
        public int Progress { get; set; }
        public string ProgressMessage { get; set; } = string.Empty;
        public string StatusMessage { get; set; } = string.Empty;
        public string StatusClass { get; set; } = string.Empty;
        public DateTime ExecutionTime { get; set; }
        public string ExecutionDuration { get; set; } = string.Empty;
        public int ServersTested { get; set; }

        /// <summary>
        /// The coverage sentence for the run that produced <see cref="Results"/>, when that run
        /// excluded one or more check categories; <c>null</c> when it excluded none.
        /// <para>Written ONCE per run, AFTER execution, by
        /// <see cref="Services.CategoryRunSelection.MeasuredDisclosure"/> from the rows the run
        /// actually produced. Not from the operator's chip state (which can change after a run
        /// starts) and not from the plan either: a run that was cancelled or lost a server skipped
        /// more than the filter did, and the plan's arithmetic would state a smaller loss than the
        /// rows beneath it demonstrate.</para>
        /// <para>It travels with <see cref="Results"/> deliberately: the on-page summary, the
        /// findings PDF and the executive briefing all read this one string, so no surface can
        /// describe the run's coverage differently from another.</para>
        /// <para>INVARIANT — it may be non-null only while <see cref="Results"/> holds exactly the
        /// rows of the run it describes. Every path that puts OTHER rows on the grid clears it:
        /// <see cref="ClearResults"/>; the page's own pre-run reset in <c>QuickCheck.RunChecks</c>;
        /// the cold restore in <c>AssessmentRehydrationService</c> (history does not record which
        /// categories a past run covered, so a restored run makes no coverage claim in either
        /// direction); and <c>QuickCheck.TopUpImportedServers</c>, which adds an imported run's
        /// rows. The accept/revoke refresh is the one path that leaves it standing, and may:
        /// it annotates the rows already present and adds none.</para>
        /// </summary>
        public string? CategoryExclusionNotice { get; set; }

        /// <summary>
        /// The completeness sentence for the run that produced <see cref="Results"/>, when that run
        /// measurably did NOT finish; <c>null</c> when it did, and <c>null</c> for rows whose run
        /// this service did not observe.
        /// <para><b>Why it exists.</b> A cancelled run, or a run one of whose planned targets never
        /// reported, produces a PDF header of counts that reads exactly like a complete assessment's.
        /// <see cref="CategoryExclusionNotice"/> could not cover it: that string is <c>null</c>
        /// whenever no category was excluded, so an UNFILTERED run that stopped early said nothing
        /// at all on the deliverable. This is ruling #4's argument (a degraded run must not produce a
        /// header that reads like a clean one) applied to the run rather than to a single check.</para>
        /// <para>Written ONCE per run, AFTER execution, by <see cref="DescribeRunCompleteness"/> from
        /// the same two counts the run measured. It carries the SAME invariant as
        /// <see cref="CategoryExclusionNotice"/> and is cleared at exactly the same sites.</para>
        /// </summary>
        public string? RunCompletenessNotice { get; set; }

        /// <summary>
        /// The one coverage string the report surfaces read: whichever of
        /// <see cref="CategoryExclusionNotice"/> and <see cref="RunCompletenessNotice"/> were
        /// measured, joined. Empty when neither was, so an unfiltered complete run's PDF is
        /// byte-identical to what it always was.
        /// </summary>
        public string CoverageNoteForReports
        {
            get
            {
                var parts = new List<string>(2);
                if (!string.IsNullOrWhiteSpace(CategoryExclusionNotice)) parts.Add(CategoryExclusionNotice!.Trim());
                if (!string.IsNullOrWhiteSpace(RunCompletenessNotice)) parts.Add(RunCompletenessNotice!.Trim());
                return string.Join(" ", parts);
            }
        }

        /// <summary>
        /// The completeness sentence, or <c>null</c> when the run finished and every planned target
        /// reported. Static and parameterised so it is driven by a test rather than only by a run.
        /// </summary>
        /// <param name="cancelled">Whether the operator stopped the run. Measured by the caller off
        /// the same cancellation token the run was driven with.</param>
        /// <param name="plannedTargets">How many targets the run intended to collect from. 0 when
        /// the plan held no checks: nothing was asked of anything, so nothing is silent.</param>
        /// <param name="reportingTargets">How many of them produced at least one row.</param>
        public static string? DescribeRunCompleteness(bool cancelled, int plannedTargets, int reportingTargets)
        {
            int silent = Math.Max(0, plannedTargets - reportingTargets);
            if (!cancelled && silent == 0) return null;

            var sentences = new List<string>(2);
            if (cancelled)
                sentences.Add("This assessment was stopped before it finished, so the counts in this "
                            + "report describe the checks that ran and not the whole plan.");
            if (silent > 0)
            {
                // Singular forms, because the most likely degraded run this app produces is ONE
                // selected server that failed to report, and that read "1 of the 1 servers in this
                // run" on the Findings PDF title band and the Executive Briefing cover. Client-facing
                // copy that cannot count is copy a reader stops trusting.
                string subject = plannedTargets == 1
                    ? "The one server in this run produced no results at all"
                    : $"{silent} of the {plannedTargets} servers in this run produced no results at all";
                string tail = silent == 1
                    ? ", so nothing in this report describes it."
                    : ", so nothing in this report describes them.";
                sentences.Add(subject + tail);
            }
            return string.Join(" ", sentences);
        }

        // ── UI preference state ───────────────────────────────────────────
        // MOVED OUT 2026-08-02 to the SCOPED QuickCheckViewState. RunAllServers /
        // SelectedConnectionId / SelectedServer are the TARGET of a privileged run, and this
        // service is process-wide — so one caller could retarget another caller's next scan.
        // The filters went with them; see Data/Services/CircuitViewState.cs.

        // ── Change notification ───────────────────────────────────────────
        // Debounced: a multi-server run completes checks fast enough that per-check
        // notifications (~12k on a 25-server run) re-render the full page thousands of
        // times and saturate the WebView2 UI thread. An isolated call (user clicks
        // Clear) still fires immediately (leading edge); a burst coalesces to at most
        // one event per debounce window, with a trailing timer guaranteeing the last
        // update always renders.
        private const int NotifyDebounceMs = 250;
        private readonly object _notifyLock = new();
        private readonly Timer _notifyTimer;
        private DateTime _lastNotifyUtc = DateTime.MinValue;
        private bool _notifyScheduled;

        public event Action? StateChanged;

        public void NotifyStateChanged()
        {
            bool fireNow = false;
            lock (_notifyLock)
            {
                var elapsedMs = (DateTime.UtcNow - _lastNotifyUtc).TotalMilliseconds;
                if (elapsedMs >= NotifyDebounceMs)
                {
                    _lastNotifyUtc = DateTime.UtcNow;
                    fireNow = true;
                }
                else if (!_notifyScheduled)
                {
                    _notifyScheduled = true;
                    _notifyTimer.Change(Math.Max(1, NotifyDebounceMs - (int)elapsedMs), Timeout.Infinite);
                }
            }
            if (fireNow)
                StateChanged?.Invoke();
        }

        private void FlushPendingNotify(object? _)
        {
            lock (_notifyLock)
            {
                if (!_notifyScheduled) return;
                _notifyScheduled = false;
                _lastNotifyUtc = DateTime.UtcNow;
            }
            StateChanged?.Invoke();
        }

        public QuickCheckStateService(CheckExecutionService? checkExecutor = null)
        {
            _checkExecutor = checkExecutor;
            if (_checkExecutor != null)
                _checkExecutor.OnCheckCompleted += HandleCheckCompleted;
            _notifyTimer = new Timer(FlushPendingNotify, null, Timeout.Infinite, Timeout.Infinite);
        }

        // Owned by the service so diagnostic logging continues after page navigation
        private void HandleCheckCompleted(CheckResult result)
        {
            // Ruling #4 (2026-07-20): WARN checked before Passed — a "could not fully assess"
            // result rides Passed=true, and logging it as a flat PASS in the live diagnostics is
            // how an operator watching the run misses that the account was under-privileged.
            var isWarn = string.IsNullOrEmpty(result.ErrorMessage)
                         && Services.CheckClassification.IsWarn(result);
            var status = !string.IsNullOrEmpty(result.ErrorMessage) ? "ERR"
                       : isWarn ? "WARN"
                       : result.Passed ? "PASS" : "FAIL";
            var level = !string.IsNullOrEmpty(result.ErrorMessage) ? "error"
                      : isWarn ? "warn"
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
            lock (_diagLogLock)
            {
                _diagLog.Add(new DiagLogEntry { Timestamp = DateTime.Now, Message = message, Level = level });

                // Evict oldest entries to prevent unbounded growth
                if (_diagLog.Count > MaxDiagLogEntries)
                    _diagLog.RemoveRange(0, _diagLog.Count - MaxDiagLogEntries);
            }

            NotifyStateChanged();
        }

        public void ClearDiagLog()
        {
            lock (_diagLogLock)
            {
                _diagLog.Clear();
            }
            NotifyStateChanged();
        }

        /// <summary>
        /// Clears the ESTATE record. The caller's own filters live on the scoped
        /// <see cref="Services.QuickCheckViewState"/> and are reset by the page
        /// (<c>QuickCheck.ClearResultsAndView</c>) — a process-wide service must not be able to
        /// reset another circuit's view.
        /// </summary>
        public void ClearResults()
        {
            _checkExecutor?.ClearAllResults();
            Results.Clear();
            ServerSummaries.Clear();
            HasRun = false;
            StatusMessage = string.Empty;
            // The notice describes the run that produced the rows just cleared; leaving it set
            // would attach a past run's coverage to whatever appears next.
            CategoryExclusionNotice = null;
            RunCompletenessNotice = null;
            NotifyStateChanged();
        }

        public void Dispose()
        {
            if (_checkExecutor != null)
                _checkExecutor.OnCheckCompleted -= HandleCheckCompleted;
            _notifyTimer.Dispose();
        }

        public class DiagLogEntry
        {
            public DateTime Timestamp { get; set; }
            public string Message { get; set; } = string.Empty;
            public string Level { get; set; } = "info";
        }
    }
}
