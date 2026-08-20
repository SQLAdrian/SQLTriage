/* In the name of God, the Merciful, the Compassionate */
/*
 * Restores the last saved Audit Assessment into QuickCheckStateService on a cold app start.
 *
 * EXTRACTED VERBATIM from QuickCheck.razor's private RehydrateLastRunFromHistory (R1,
 * 2026-07-07; seat gate 2026-07-17; #84 imported-run precedence) on 2026-07-30, because the
 * defect class it fixes is not page-local: any surface that reads QuickCheckStateService.Results
 * directly (/compliance-map and /compliance-tree did) renders its empty state after an app
 * restart until the USER happens to visit /audit first. The rehydrate is state-population, so
 * it lives with the state, not with whichever page historically triggered it.
 *
 * The rules in here were each earned by a live defect — seat gating (a 1-seat licence still
 * rendered 1150 rows), acceptance annotate-on-read, scored-basis summaries (SKIP/INFO were
 * inflating Passed), and #84's newest-run-wins import precedence. Change them only with the
 * matching ruling in hand.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services
{
    public class AssessmentRehydrationService
    {
        private readonly QuickCheckStateService _state;
        private readonly ServerConnectionManager _connections;
        private readonly ISeatRegister _seats;
        private readonly GovernanceHistoryService _govHistory;
        private readonly AcceptedFindingsService _acceptedFindings;
        private readonly CheckExecutionService _checkExecutor;

        public AssessmentRehydrationService(
            QuickCheckStateService state,
            ServerConnectionManager connections,
            ISeatRegister seats,
            GovernanceHistoryService govHistory,
            AcceptedFindingsService acceptedFindings,
            CheckExecutionService checkExecutor)
        {
            _state = state;
            _connections = connections;
            _seats = seats;
            _govHistory = govHistory;
            _acceptedFindings = acceptedFindings;
            _checkExecutor = checkExecutor;
        }

        /// <summary>
        /// The seat-exclusion banner text produced by the most recent rehydrate pass; null when
        /// nothing was dropped. /audit renders it verbatim; other callers may ignore it (they
        /// carry their own seat filters over the restored results).
        /// </summary>
        public string? LastSeatNotice { get; private set; }

        /// <summary>
        /// Idempotent cold-start guard: restores the last saved assessment ONLY when nothing is
        /// in memory and no run is in progress. Safe to call from every page that consumes
        /// <see cref="QuickCheckStateService.Results"/> — a warm state returns immediately.
        /// Returns true when a restore actually happened.
        /// </summary>
        public bool EnsureLastRunLoaded()
        {
            if (_state.HasRun || _state.IsRunning || _state.Results.Count > 0) return false;
            return RehydrateLastRunFromHistory();
        }

        // Restore the last recorded assessment (newest row per check per server, <=7 days old)
        // and re-apply the acceptance overlay — history stores the truthful result; acceptance
        // is annotate-on-read.
        private bool RehydrateLastRunFromHistory()
        {
            try
            {
                var restored = new List<CheckResult>();
                DateTime? newest = null;
                // #84: the local run time per configured server, so Pass 2 (below) can apply honest
                // newest-run-wins precedence when the same instance also has an imported run.
                var localRunTimeByServer = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
                // ── Instance-seat gate ────────────────────────────────────────────────────
                // This pass reads GovernanceHistoryService DIRECTLY, so it does NOT go through
                // CheckExecutionService.GetResults and therefore does NOT inherit that method's seat
                // filter. Without this an unseated ("uncoded") instance keeps reporting its last known
                // run forever — live-proven 2026-07-17: a 1-seat licence correctly REFUSED
                // .\OLD2017 at claim time, yet the grid still rendered 1150 rows (2 x 575) across both.
                // Pass 2 below is already safe (it goes through GetResults).
                //
                // Filter over the CONFIGURED servers this pass actually walks, not over
                // GetServerSeatFilter()'s results-derived set: a server can have GovHistory rows without
                // a JSON-store run, and it must still be gated (and named in the banner) here.
                // NOT a silent drop — LastSeatNotice is rendered verbatim above the /audit grid.
                var configured = _connections.GetEnabledConnections()
                    .SelectMany(c => c.GetServerList())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var seatFilter = _seats.Filter(configured);
                var seatedSet  = new HashSet<string>(seatFilter.Seated, StringComparer.OrdinalIgnoreCase);
                LastSeatNotice = seatFilter.ExclusionNotice;   // null when nothing was dropped

                foreach (var conn in _connections.GetEnabledConnections())
                {
                    foreach (var server in conn.GetServerList())
                    {
                        if (!seatedSet.Contains(server)) continue;

                        var rows = _govHistory.LoadLastKnownResults(server, out var ts);
                        if (rows.Count == 0) continue;
                        localRunTimeByServer[server] = ts;
                        foreach (var r in rows.Where(r => !r.Passed))
                        {
                            var acc = _acceptedFindings.GetAcceptance(server, r.CheckId);
                            if (acc != null)
                            {
                                r.IsAccepted          = true;
                                r.AcceptanceReason    = acc.Reason;
                                r.AcceptedBy          = acc.AcceptedBy;
                                r.AcceptanceExpiresAt = acc.ExpiresAt;
                            }
                        }
                        restored.AddRange(rows);
                        if (ts != null && (newest == null || ts > newest)) newest = ts;
                        // 2026-07-16: scored basis (CheckClassification) — this had NO SKIP/INFO
                        // exclusion at all, so a restored run's Passed count included every SKIP/INFO
                        // result (CheckExecutionService sets Passed=true for both), inflating it past
                        // what /governance's scored PassedFindings shows for the identical run. Passed
                        // stays raw r.Passed (mutually exclusive from Accepted) — matches PassedCount/
                        // AcceptedCount/FailedCount and CheckExecutionService's own live tally.
                        _state.ServerSummaries.Add(new CheckExecutionSummary
                        {
                            InstanceName  = server,
                            StartedAt     = ts ?? DateTime.UtcNow,
                            CompletedAt   = ts ?? DateTime.UtcNow,
                            TotalChecks   = rows.Count,
                            Informational = rows.Count(r => !CheckClassification.IsScorable(r)),
                            // Subset of Informational (2026-07-21) — see CheckExecutionSummary.Partial.
                            Partial       = rows.Count(r => CheckClassification.IsWarn(r)),
                            Passed        = rows.Count(r => CheckClassification.IsScorable(r) && r.Passed),
                            Accepted      = rows.Count(r => CheckClassification.IsScorable(r) && !r.Passed && r.IsAccepted),
                            Failed        = rows.Count(r => CheckClassification.IsScorable(r) && !r.Passed && !r.IsAccepted)
                        });
                    }
                }

                // ── #84 Pass 2: imported runs ────────────────────────────────────────────────
                // A server whose results were IMPORTED (Pages/ImportResults.razor / --import) lands in
                // the QuickCheck JSON store but is NOT a configured connection, so the GovHistory pass
                // above never discovers it. Walk every server the store knows about and load it through
                // the SAME CheckExecutionService.GetResults path local runs use (shared CheckClassification
                // pipeline — no separate counting path). Precedence is honest newest-run-wins: an imported
                // run only displaces a configured server's local run when it is strictly newer, and it is
                // never merged into the local run's rows — one run wins outright.
                foreach (var server in _checkExecutor.GetServersWithResults())
                {
                    if (string.IsNullOrWhiteSpace(server)) continue;
                    var rows = _checkExecutor.GetResults(server, int.MaxValue);
                    if (rows.Count == 0 || !ImportProvenance.RunIsImported(rows)) continue; // only imported runs

                    var importedAt = ImportProvenance.RunImportTime(rows) ?? DateTime.UtcNow;
                    var isCovered = localRunTimeByServer.TryGetValue(server, out var localTs);
                    if (isCovered && !ImportProvenance.ImportedRunWins(importedAt, localTs))
                        continue; // the configured server's local run is newer/equal — keep it

                    if (isCovered)
                    {
                        // Imported run is strictly newer — displace the local run ENTIRELY (never a merge).
                        restored.RemoveAll(r => string.Equals(r.InstanceName, server, StringComparison.OrdinalIgnoreCase));
                        _state.ServerSummaries.RemoveAll(s => string.Equals(s.InstanceName, server, StringComparison.OrdinalIgnoreCase));
                    }

                    restored.AddRange(rows);
                    _state.ServerSummaries.Add(BuildRunSummary(server, rows, importedAt));
                    if (newest == null || importedAt > newest) newest = importedAt;
                }

                if (restored.Count == 0) return false;

                _state.Results           = restored;
                _state.HasRun            = true;
                // These rows came out of history, not out of a run this page assembled, and history
                // does not record which categories that run covered. So the coverage notice is
                // cleared rather than inherited: a restored run must not carry the live run's
                // exclusion sentence, and it must not be handed one it cannot substantiate either.
                _state.CategoryExclusionNotice = null;
                // Same argument for completeness: history does not record whether that run finished.
                _state.RunCompletenessNotice = null;
                _state.ServersTested     = _state.ServerSummaries.Count;
                _state.ExecutionTime     = (newest ?? DateTime.UtcNow).ToLocalTime();
                _state.ExecutionDuration = "n/a (restored)";
                _state.Categories        = restored.Select(r => r.Category).Distinct().OrderBy(c => c).ToList();
                _state.StatusMessage     = $"Showing last saved assessment from {(newest?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "history")} — run a new assessment to refresh.";
                _state.StatusClass       = "warning";
                _state.AddDiagEntry($"Restored {restored.Count} result(s) from history for {_state.ServersTested} server(s).", "info");
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to rehydrate last assessment from history");
                return false;
            }
        }

        /// <summary>
        /// Shared per-server restore summary — the SAME CheckClassification basis the rehydrate
        /// loop uses. Public static so QuickCheck's #84 imported-run top-up composes the identical
        /// summary rather than growing a second counting path.
        /// </summary>
        public static CheckExecutionSummary BuildRunSummary(string server, IReadOnlyList<CheckResult> rows, DateTime when) =>
            new CheckExecutionSummary
            {
                InstanceName  = server,
                StartedAt     = when,
                CompletedAt   = when,
                TotalChecks   = rows.Count,
                Informational = rows.Count(r => !CheckClassification.IsScorable(r)),
                // Subset of Informational (2026-07-21) — see CheckExecutionSummary.Partial.
                Partial       = rows.Count(r => CheckClassification.IsWarn(r)),
                Passed        = rows.Count(r => CheckClassification.IsScorable(r) && r.Passed),
                Accepted      = rows.Count(r => CheckClassification.IsScorable(r) && !r.Passed && r.IsAccepted),
                Failed        = rows.Count(r => CheckClassification.IsScorable(r) && !r.Passed && !r.IsAccepted)
            };
    }
}
