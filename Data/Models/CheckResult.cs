/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// Result of executing a SQL check against a specific instance.
    /// </summary>
    public class CheckResult
    {
        public string CheckId { get; set; } = string.Empty;
        public string CheckName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public int ActualValue { get; set; }
        public int ExpectedValue { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// True when the check's SQL failed its integrity checksum and was BLOCKED
        /// (not executed). Distinct from Passed/Failed/Error — surfaced separately
        /// in the UI as "Corrupted". See CheckSqlStore.Verify (S1).
        /// </summary>
        public bool IsCorrupted { get; set; }

        /// <summary>
        /// F6: true when this not-passed finding has a client-configured acceptance
        /// on this instance (accepted-by-design). The STORED result stays truthful
        /// (Passed stays false); this flag is applied ON READ in
        /// CheckExecutionService.GetResults. Accepted findings ride the Passed tier
        /// for score math (GovernanceService CountsAsPass) and are badged — never hidden.
        /// See AcceptedFindingsService.
        /// </summary>
        public bool IsAccepted { get; set; }

        /// <summary>F6: the required audit narrative for the acceptance (badge tooltip).</summary>
        public string? AcceptanceReason { get; set; }

        /// <summary>F6: who recorded the acceptance (badge tooltip).</summary>
        public string? AcceptedBy { get; set; }

        /// <summary>F6: when the acceptance expires (badge tooltip); null = never.</summary>
        public DateTime? AcceptanceExpiresAt { get; set; }

        /// <summary>
        /// The SQL Server instance this check was executed against.
        /// </summary>
        public string InstanceName { get; set; } = string.Empty;

        public double EffortHours { get; set; }

        /// <summary>
        /// Mirrors the check DEFINITION's <see cref="SqlCheck.IsBad"/> flag (fundable-risk
        /// classification — see ReportBundleService's Risk Register), copied onto every execution
        /// row at build time (CheckExecutionService.ExecuteSingleCheckAsync). It is NOT a verdict
        /// on this result: a PASS row carries the same value as a FAIL row for the same check, so
        /// a JSON export reader must not read it as "this finding is bad" — it says "the check
        /// this row came from is costed as fundable risk when it fails". Named for the export
        /// payload (was <c>IsBad</c> until 2026-08-20); the CSV export deliberately omits this
        /// column for the same reason (see CsvResultWriter's Status-vocabulary header comments).
        /// </summary>
        public bool DefinitionFlaggedAdverse { get; set; }

        /// <summary>
        /// READ-COMPAT ONLY for run files written before the 2026-08-20 rename (509143c), which
        /// carry <c>"IsBad"</c> where this build writes <c>"DefinitionFlaggedAdverse"</c>. Without
        /// it those files deserialize the flag to false silently — measured 2026-09-06 on this box:
        /// about 70 of 84 stored QuickCheckResultStore run files still carried the old key (the
        /// builder counted 71/85, the cold gate re-counted 70/84 the same day; one file of drift).
        /// Known limits, on record: a document carrying BOTH keys is resolved by document order
        /// (the later one wins) — zero such files exist on this box (checked 2026-09-08, lane
        /// hygiene-tail, at C:/SQLTriage-Service/output/quickcheck: 10 of 10 run files carry
        /// DefinitionFlaggedAdverse, 0 carry IsBad, 0 carry both — the store has fully rolled
        /// past the old key since the 2026-09-06 70-of-84 measurement, RetentionPerServer=10).
        /// Set-only on purpose: System.Text.Json uses a property with no getter for DESERIALIZATION
        /// and skips it when writing, so the export keeps exactly one key for this flag. That is
        /// pinned by QuickCheckResultStorePathTests.WriteRun_ExportsDefinitionFlaggedAdverse_NotIsBad.
        /// Read both, write new — do not add a getter.
        ///
        /// A NUMERIC "IsBad" used to fail deserialization outright (it was an ignored unknown
        /// member before the rename, so this was a new failure mode). RESOLVED 2026-09-08 (lane
        /// hygiene-tail): <see cref="LegacyBoolConverter"/> now accepts bool, 0/1, and
        /// "true"/"false" strings for this one property, because BOTH warranting conditions are
        /// true on this box — the results-import path (QuickCheckResultStore.TryParseRunPayload)
        /// reads files from OUTSIDE this process, and QuickCheckResultStore.GetServersWithRuns has
        /// a bare catch with no log around the same deserialization, so a numeric legacy key would
        /// otherwise drop a server from every server-with-runs listing SILENTLY.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("IsBad")]
        [System.Text.Json.Serialization.JsonConverter(typeof(LegacyBoolConverter))]
        public bool LegacyIsBad { set => DefinitionFlaggedAdverse = value; }

        public int ScoreWeight { get; set; } = 1;

        /// <summary>
        /// Duration of the check execution in milliseconds.
        /// </summary>
        public long DurationMs { get; set; }

        /// <summary>
        /// Recommended action from the check definition, shown when a check fails.
        /// </summary>
        public string? RecommendedAction { get; set; }

        /// <summary>
        /// Description from the check definition.
        /// </summary>
        public string? Description { get; set; }

        public string? Eli5Description { get; set; }
        public string? Eli5Remediation { get; set; }
        public string? BusinessImpact { get; set; }

        /// <summary>
        /// #49 (2026-07-15): the RAW verdict text a verdict-contract check's SQL returned
        /// (e.g. "PASS", "FAIL", "INFO", "WARN", "SKIP") — distinct from <see cref="Severity"/>
        /// (the check's own declared severity level, e.g. "Medium"). PASS/INFO/SKIP all map to
        /// <see cref="Passed"/>=true for scoring (unchanged, see CheckExecutionService), but a
        /// check whose own body documents a three-way PASS/FAIL/INFO contract can genuinely
        /// return INFO — an informational note with a computed recommendation, not an assertion
        /// the control is correctly configured. Without this field the UI had no way to tell
        /// "true PASS" from "INFO riding the passed tier" and rendered both as a bare "Pass"
        /// badge. Null for numeric-contract checks and for results recorded before this field
        /// existed (older persisted JSON) — those fall back to the pre-existing Passed-only
        /// rendering, unchanged.
        /// </summary>
        public string? Verdict { get; set; }

        // ── #84 results-import provenance (2026-07-17) ──
        // Stamped by ImportResultsService when a run is IMPORTED into this build
        // (Pages/ImportResults.razor or SQLTriage.exe --import) rather than executed live here.
        // Both are nullable + additive so existing persisted JSON (QuickCheckResultStore) round-
        // trips unchanged — a locally-executed result leaves them null. When set, the /audit
        // results grid badges the row "Imported" (with date + source file) so a reader can never
        // mistake an imported result for a locally-executed one. Classification (CheckClassification)
        // never reads these — an imported result is bucketed Pass/Fail/Skip/Info exactly as a local
        // one, so the two flow through the identical scoring path.

        /// <summary>UTC time this result was imported into THIS build's store; null for a
        /// locally-executed result. See ImportProvenance / ImportResultsService.</summary>
        public DateTime? ImportedAtUtc { get; set; }

        /// <summary>Source file name the imported run was read from (e.g. "PROD01-20260717.json");
        /// null for a locally-executed result. Honesty surface only — shown in the Imported badge
        /// tooltip so the provenance of an imported row is fully traceable.</summary>
        public string? ImportSourceFile { get; set; }

        // ── #28 diagnostic (2026-07-13) — full SqlException capture ──
        // Populated only when this check's ErrorMessage came from a Microsoft.Data.SqlClient
        // .SqlException (or an exception wrapping one); null otherwise. Nullable + additive so
        // existing persisted JSON (QuickCheckResultStore) round-trips unchanged for runs that
        // predate this field. See CheckExecutionService.ApplySqlExceptionDetails.
        public int? SqlErrorNumber { get; set; }
        public byte? SqlErrorClass { get; set; }
        public byte? SqlErrorState { get; set; }
        public int? SqlErrorLineNumber { get; set; }
        public string? SqlErrorServer { get; set; }
    }

    /// <summary>
    /// Summary of a check execution run across one or more instances.
    /// </summary>
    public class CheckExecutionSummary
    {
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public string InstanceName { get; set; } = string.Empty;
        public int TotalChecks { get; set; }
        public int Passed { get; set; }

        /// <summary>Open findings only. F6: client-accepted findings are counted in
        /// <see cref="Accepted"/>, not here, so run summaries agree with the results grid.</summary>
        public int Failed { get; set; }

        /// <summary>F6: not-passed findings with a live client acceptance at run time.</summary>
        public int Accepted { get; set; }

        public int Errors { get; set; }

        /// <summary>
        /// 2026-07-16: checks whose Verdict/Severity is INFO, or whose Message starts "SKIP" —
        /// see <see cref="SQLTriage.Data.Services.CheckClassification.IsScorable"/>. Previously
        /// these rode <see cref="Passed"/>++ (CheckExecutionService sets Passed=true for both
        /// SKIP and INFO), inflating this summary's Passed count above what /governance's scored
        /// PassedFindings showed for the identical run. Additive field — TotalChecks == Passed +
        /// Failed + Accepted + Errors + Informational for runs tallied after this change; older
        /// persisted summaries (before this field existed) still foot the old way (Informational
        /// stays 0, Passed still carries the inflated count) since they were never re-tallied.
        /// </summary>
        public int Informational { get; set; }

        /// <summary>
        /// 2026-07-21: checks whose Verdict is WARN — the state every user-facing surface labels
        /// <b>Partial</b> (the check ran but could not fully assess the target). A SUBSET of
        /// <see cref="Informational"/>, deliberately NOT a new additive bucket: the footing
        /// invariant TotalChecks == Passed + Failed + Accepted + Errors + Informational is
        /// unchanged, and older persisted summaries stay valid with Partial == 0.
        ///
        /// It exists because the row grid named this state and every roll-up above it did not:
        /// /cio and /dba folded WARN silently into "Informational/skipped", so a state introduced
        /// precisely to stop a degraded run reading as clean became invisible on the two screens a
        /// CIO and a DBA actually read. Surfaces render Informational MINUS Partial alongside
        /// Partial, so the displayed buckets are disjoint and still sum to TotalChecks — a
        /// decomposition of a number already on the page, never an extra headline figure.
        /// </summary>
        public int Partial { get; set; }

        public TimeSpan Duration => CompletedAt - StartedAt;
    }
}
