/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationTemplate — a registered, bounded, reversible fix the gated
 * remediation lane is allowed to apply. Build step 3 of the lane.
 *
 * A template is the ONLY thing that authorises a write (see
 * SqlSafetyValidator.Classify). It carries everything the runner (step 4) needs
 * to apply a fix safely and prove it:
 *   - the dbatools command that performs the change (with -WhatIf preview),
 *   - a snapshot query to capture pre-change state (for verify + rollback),
 *   - a verify query to confirm the post-change state,
 *   - its risk class, which structurally isolates sensitive ops onto their own
 *     keys (a sensitive op can never be reached through a standard handler).
 *
 * Templates are SHIPPED, not user-authored. The store seeds them in code so the
 * lane works with no JSON file present; an optional overlay file is supported
 * via the same atomic-persist pattern as RemediationWeightStore.
 */

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Risk tier of a remediation. Drives structural isolation: a
    /// <see cref="Sensitive"/> op rides its own template key and can never be
    /// reached through a <see cref="Trivial"/> / <see cref="Standard"/> handler.
    /// </summary>
    public enum RemediationRiskClass
    {
        /// <summary>Always-safe, idempotent, instantly reversible (e.g. a config toggle).</summary>
        Trivial,
        /// <summary>Bounded change with a clear pre/post state and rollback (e.g. MAXDOP).</summary>
        Standard,
        /// <summary>Destructive or security-affecting; isolated handler, extra gates.</summary>
        Sensitive
    }

    /// <summary>
    /// How a template's change is applied — which drives gate-5 rollback strategy.
    /// </summary>
    public enum RemediationKind
    {
        /// <summary>
        /// A server/database configuration change (sp_configure / RECONFIGURE).
        /// SQL Server forbids RECONFIGURE inside a user transaction, so rollback is
        /// snapshot-based: capture the old value, and on rollback re-apply it.
        /// </summary>
        Configuration,
        /// <summary>
        /// A DDL/DML change that CAN run inside a transaction. Gate 5 wraps it in
        /// SET XACT_ABORT ON / BEGIN TRAN so a verify failure rolls back atomically.
        /// </summary>
        Transactable
    }

    /// <summary>
    /// Coarse fix category — drives the per-fix power/energy-savings band shown in the
    /// remediation result and green readouts. Illustrative bands (see
    /// <see cref="PowerEstimateService.ReductionBand"/>), not per-server promises.
    /// </summary>
    public enum RemediationType
    {
        /// <summary>Config toggle (e.g. CTFP) — small CPU effect (~3-10%).</summary>
        Config,
        /// <summary>MAXDOP / parameter-sniffing fixes — moderate CPU effect (~5-25%).</summary>
        MaxdopParamSniff,
        /// <summary>Index add / rebuild — largest CPU + read effect (~10-40%).</summary>
        IndexAddRebuild,
        /// <summary>I/O reduction — storage / latency effect (~10-35%).</summary>
        IoReduction,
        /// <summary>
        /// Plan-quality / cardinality fixes — stale or missing statistics, and implicit
        /// conversions that defeat an index seek. Restoring good cardinality estimates
        /// re-shapes plans and cuts CPU + reads (~10-30%).
        /// </summary>
        PlanQuality
    }

    /// <summary>
    /// The kind of structured change an <see cref="RemediationOperation"/> describes.
    /// The renderer (<c>RemediationOpRenderer</c>) turns the op into the EXACT T-SQL
    /// the gate classifies and the executor runs — one render, one source of truth.
    /// MVP ships only <see cref="SpConfigure"/>.
    /// </summary>
    public enum RemediationOpKind
    {
        /// <summary>
        /// A server-level <c>sp_configure</c> setting + <c>RECONFIGURE</c>. The setting
        /// name is shipped (never user input); only the integer value is bound, and it is
        /// bounds-checked. Rollback re-applies the captured pre-change value.
        /// </summary>
        SpConfigure,

        /// <summary>
        /// Create a missing index (<c>CREATE INDEX</c>). The index spec (database / schema /
        /// table / name / key + included columns) is supplied per-request from a missing-index
        /// DMV candidate — every identifier is charset-guarded AND bracket-quoted, so the rendered
        /// DDL is injection-free by construction. Rollback is the clean inverse: <c>DROP INDEX</c>
        /// the index we created (no BEGIN TRAN gymnastics; CREATE INDEX is its own atomic unit).
        /// This is the first write past sp_configure — gated as Remediation only under the single
        /// registered <c>ADDMISSINGINDEX</c> key.
        /// </summary>
        CreateIndex,

        /// <summary>
        /// The standard SQL Agent alert + operator pack: one operator (name + email), ten
        /// severity/error-number <c>sp_add_alert</c> alerts, and an email <c>sp_add_notification</c>
        /// per alert. Every CREATE is guarded <c>IF NOT EXISTS</c> so re-apply is a no-op
        /// (idempotent by construction). No bound integer value — the operator name/email are
        /// the only per-request parameters, resolved via <c>RemediationOpRenderer.AgentAlertPack*Param</c>.
        /// Gated off entirely when SQL Agent is unavailable (Express edition has no Agent).
        /// </summary>
        AgentAlertPack,

        /// <summary>
        /// Installs Ola Hallengren's Maintenance Solution (CommandExecute, DatabaseBackup,
        /// DatabaseIntegrityCheck, IndexOptimize procs + CommandLog table) from an embedded,
        /// checksum-pinned copy of the official script, WITHOUT creating jobs (<c>@CreateJobs='N'</c>
        /// — jobs are created separately, per-lane, by the four schedule tickboxes so the operator
        /// controls exactly which lanes run). Snapshot = do the 4 core procs already exist? If so,
        /// apply is a NoOp (the 9515-line script is NEVER re-run over an existing install — see
        /// <c>MaintenanceSolutionOpRenderer</c>). Rollback (uninstall) is a NAMED inverse: DROP the 4
        /// procs + CommandLog, and drop only the jobs THIS apply's schedule tickboxes created — never
        /// a pre-existing Ola install. Gated off (procs only, no schedule tickboxes) when SQL Agent is
        /// unavailable (Express) — the procs themselves work without Agent.
        /// </summary>
        InstallMaintenanceSolution,

        /// <summary>
        /// Lane S5 — gated ONE-SHOT BACKUP DATABASE against the LIVE server. The
        /// database/directory ride request parameters (guarded/escaped by
        /// BackupCheckDbOpRenderer); a REQUIRED confirm token
        /// (BackupCheckDbOpRenderer.ConfirmLargeOperationParam) and a resource gate
        /// (estimated backup size vs the target drive's free space, via DiskIoService)
        /// gate the apply BEFORE any T-SQL is rendered for real. NOT reversible — a
        /// completed backup has no "undo" (nor does it need one: it only WRITES a new
        /// file, never touches existing data), so Reversible=false on the template is
        /// an honest statement, not a safety gap.
        /// </summary>
        BackupDatabaseNow,

        /// <summary>
        /// Lane S5 — gated ONE-SHOT DBCC CHECKDB against the LIVE server. Same
        /// confirm-token + resource-gate contract as BackupDatabaseNow, sized off the
        /// CHECKDB internal-snapshot space heuristic (BackupCheckDbOpRenderer.
        /// CheckDbRecommendedFreeFraction / CheckDbHardFloorFreeFraction) rather than a
        /// backup-size estimate. NOT reversible — CHECKDB is a read-only consistency
        /// pass (no repair option is ever rendered), so there is nothing to roll back.
        /// </summary>
        CheckDbNow,

        /// <summary>
        /// A per-database <c>ALTER DATABASE [name] SET &lt;option&gt;</c> change (corpus
        /// checks #39 — 12 checks, e.g. DB_CHAINING, AUTO_CLOSE, TRUSTWORTHY). Target
        /// databases come SOLELY from the op's <see cref="RemediationOperation.OffendersQuery"/>
        /// (read-only) — never operator-typed. Same Configuration-kind rationale as
        /// SpConfigure: ALTER DATABASE ... SET auto-commits (cannot run inside a user
        /// transaction), so rollback is snapshot-based (re-apply the inverse clause to
        /// every database this apply changed).
        /// </summary>
        DbSetOption,

        /// <summary>
        /// Injects an availability-group primary-replica guard as step 1 of an existing SQL
        /// Agent job, so the job's real work only runs on the replica that currently owns the
        /// database. The guard body is the operator's own T-SQL; the app supplies exactly one
        /// substitution — the database name — which is charset-guarded and literal-escaped.
        /// <para>
        /// Idempotent: the apply is wrapped <c>IF NOT EXISTS</c> on a guard already sitting at
        /// step 1, so re-running is a strict no-op (the executor short-circuits it to
        /// <c>NoOp</c>, which refunds the credit). Reversible: the inverse deletes the step this
        /// apply added and restores the job's original <c>start_step_id</c>.
        /// </para>
        /// <para>
        /// Only offered for jobs whose steps use quit / fall-through flow actions. Jobs wired
        /// with explicit "go to step N" targets are refused and surfaced for manual review —
        /// inserting a step there would leave another DBA's control flow pointing at the wrong
        /// step. See <see cref="Models.Jobs.AgentJobDefinition.EligibleForGuard"/>.
        /// </para>
        /// </summary>
        AgentJobPrimaryGuard,

        /// <summary>
        /// Recreates ONE Agent job on an availability-group secondary from the primary's
        /// definition (drop-and-recreate; see <c>AgentJobSyncOpRenderer</c> for why, and for the
        /// two-pass step wiring). Directional only — primary is read, secondary is written.
        /// Reversible via the snapshot of the secondary's prior definition; the job's RUN HISTORY
        /// on the secondary is not recoverable, which the preview states before approval.
        /// </summary>
        AgentJobSync,

        /// <summary>
        /// Deletes a job that exists ONLY on the secondary. Deliberately a separate op kind and
        /// template key from <see cref="AgentJobSync"/> so a sync can never delete anything: this
        /// is opt-in per row, Sensitive, and not reversible (msdb keeps no copy of a dropped job).
        /// </summary>
        AgentJobDeleteExtra
    }

    /// <summary>
    /// The structured, bounded change a template authorises — the SINGLE SOURCE the
    /// gate classifies and the executor runs. Carrying the real operation (rather than a
    /// hard-coded authorisation probe) is what makes the safety gate vet WHAT ACTUALLY
    /// RUNS: the runner renders this op to T-SQL and classifies that exact rendering;
    /// the executor renders the same op (with the bounds-checked value) and executes it.
    ///
    /// The configuration NAME is shipped (immutable, never untrusted input) and the only
    /// bound parameter is an integer constrained to [<see cref="MinValue"/>,
    /// <see cref="MaxValue"/>], so the rendered T-SQL is injection-free by construction.
    /// </summary>
    public sealed class RemediationOperation
    {
        [JsonPropertyName("opKind")]
        public RemediationOpKind OpKind { get; set; } = RemediationOpKind.SpConfigure;

        /// <summary>
        /// The sp_configure setting name (shipped, never user input), e.g.
        /// <c>"max degree of parallelism"</c>.
        /// </summary>
        [JsonPropertyName("configName")]
        public string ConfigName { get; set; } = string.Empty;

        /// <summary>
        /// Whether this setting is an "advanced option" — if so the rendered batch first
        /// enables <c>show advanced options</c> (the prerequisite for setting it via
        /// sp_configure). MAXDOP is an advanced option.
        /// </summary>
        [JsonPropertyName("advancedOption")]
        public bool AdvancedOption { get; set; } = true;

        /// <summary>
        /// Which request parameter carries the target integer value (e.g. <c>"MaxDop"</c>).
        /// The executor resolves and bounds-checks this at apply time.
        /// </summary>
        [JsonPropertyName("valueParam")]
        public string ValueParam { get; set; } = string.Empty;

        /// <summary>Inclusive lower bound for the target value.</summary>
        [JsonPropertyName("minValue")]
        public int MinValue { get; set; }

        /// <summary>Inclusive upper bound for the target value.</summary>
        [JsonPropertyName("maxValue")]
        public int MaxValue { get; set; } = 64;

        /// <summary>
        /// A fixed target value with no operator input (corpus `value_fixed`) — mutually
        /// exclusive with <see cref="ValueParam"/>. When set, the executor resolves this value
        /// directly (see <c>RemediationOpRenderer.TryResolveValue</c>) instead of requiring a
        /// request parameter.
        /// </summary>
        [JsonPropertyName("valueFixed")]
        public int? ValueFixed { get; set; }

        /// <summary>
        /// The value this fix EXISTS to reach — the target its own description declares
        /// ("Enable ... (1)", "Disable ... (0)", corpus <c>value_fixed</c>). Two jobs:
        /// <list type="number">
        /// <item>it is what the UI pre-populates, so a one-click fix never offers the operator a
        /// value that undoes itself (the shipped default used to be <see cref="MinValue"/> — the
        /// schema FLOOR, which for every "Enable (1)" toggle is exactly 0, the opposite of the fix);</item>
        /// <item>it is the direction the executor guards, so an apply that would move a server
        /// already AT this value away from it is refused rather than written and charged for.</item>
        /// </list>
        /// NULL means the app has no shipped basis to recommend a value (MAXDOP, cost threshold
        /// for parallelism, and a max-server-memory cap are per-workload judgments, not constants).
        /// A null here is honest: the operator supplies the target, and nothing is pre-filled.
        /// </summary>
        [JsonPropertyName("recommendedValue")]
        public int? RecommendedValue { get; set; }

        /// <summary>
        /// DbSetOption kind: the shipped <c>ALTER DATABASE [name] &lt;OptionSql&gt;;</c> suffix
        /// (e.g. <c>"SET DB_CHAINING OFF"</c>) — never user input.
        /// </summary>
        [JsonPropertyName("optionSql")]
        public string? OptionSql { get; set; }

        /// <summary>
        /// DbSetOption kind: the read-only query returning exactly the databases needing the
        /// fix — the SOLE source of target databases for both preview and apply (parity by
        /// construction: both render from this same query).
        /// </summary>
        [JsonPropertyName("offendersQuery")]
        public string? OffendersQuery { get; set; }
    }

    /// <summary>
    /// A registered remediation. Immutable once constructed; the store owns the set.
    /// </summary>
    public class RemediationTemplate
    {
        /// <summary>
        /// Stable registration key (e.g. <c>"MAXDOP"</c>). Matches the key
        /// SqlSafetyValidator recognises to promote a write to Remediation.
        /// UPPERCASE, no spaces — this is the authorisation token, not a label.
        /// </summary>
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        /// <summary>Human-readable name for the approval UI.</summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>One-line description of what the fix does.</summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("riskClass")]
        public RemediationRiskClass RiskClass { get; set; } = RemediationRiskClass.Standard;

        /// <summary>How the change applies; drives gate-5 rollback strategy.</summary>
        [JsonPropertyName("kind")]
        public RemediationKind Kind { get; set; } = RemediationKind.Configuration;

        /// <summary>Coarse fix category — drives the per-fix power-savings band.</summary>
        [JsonPropertyName("type")]
        public RemediationType Type { get; set; } = RemediationType.Config;

        /// <summary>
        /// Whether to show the modelled power/tuning-headroom band for this fix. FALSE for fixes
        /// that don't reduce CPU/I-O WORK (e.g. a max-memory cap is a stability fix) — showing a
        /// "could cut ~X% CPU" band there would overstate. Honesty: under-claim by suppression.
        /// </summary>
        [JsonPropertyName("showPowerBand")]
        public bool ShowPowerBand { get; set; } = true;

        /// <summary>
        /// The dbatools command that performs the change. Run with <c>-WhatIf</c>
        /// for gate-4 preview, then for real on approval (gate 5). Parameters are
        /// supplied by the runner; this is the command identity, e.g.
        /// <c>Set-DbaMaxDop</c>.
        /// </summary>
        [JsonPropertyName("dbatoolsCommand")]
        public string DbatoolsCommand { get; set; } = string.Empty;

        /// <summary>
        /// Read-only T-SQL capturing the pre-change state. Its result is the
        /// rollback target and the verify baseline. MUST classify as Safe.
        /// </summary>
        [JsonPropertyName("snapshotQuery")]
        public string SnapshotQuery { get; set; } = string.Empty;

        /// <summary>
        /// Read-only T-SQL re-read after the change to confirm it took effect.
        /// MUST classify as Safe.
        /// </summary>
        [JsonPropertyName("verifyQuery")]
        public string VerifyQuery { get; set; } = string.Empty;

        /// <summary>
        /// Whether the change can be reversed to the snapshot state. Step 3 ships
        /// only reversible templates; the runner refuses to apply a non-reversible
        /// template unless its risk class explicitly permits it.
        /// </summary>
        [JsonPropertyName("reversible")]
        public bool Reversible { get; set; } = true;

        /// <summary>
        /// Explicit credit price for one apply, overriding the derived
        /// <see cref="Remediation.RemediationCreditCost"/> formula. Null (the default) means
        /// "derive from <see cref="RiskClass"/> + <see cref="Reversible"/>", which is what
        /// almost every template should do. Set it only where the derived price is honestly
        /// wrong for the template — e.g. a per-job sweep where each job is one cheap unit.
        /// Clamped to [1, 5] on read; a corpus-authored value out of range is ignored.
        /// </summary>
        [JsonPropertyName("creditCost")]
        public int? CreditCostOverride { get; set; }

        /// <summary>
        /// The structured change this template authorises — the SINGLE SOURCE the gate
        /// classifies and the executor runs (see <see cref="RemediationOperation"/>).
        /// Required for a <see cref="RemediationKind.Configuration"/> template: the gate
        /// renders it to T-SQL, classifies that exact text, and the executor runs the
        /// same rendering. Null only for legacy/other kinds that apply via dbatools.
        /// </summary>
        [JsonPropertyName("operation")]
        public RemediationOperation? Operation { get; set; }

        /// <summary>
        /// Corpus check ids this fix resolves (e.g. <c>"SQLT-BLITZ-NO-OPERATORS"</c>). Informational
        /// only — no runtime logic keys off it. Lets the roadmap/findings surface point an operator
        /// from a failed check straight at the fix that would clear it. Empty for templates authored
        /// before this field existed (never guessed after the fact; populated only when verified
        /// against the corpus).
        /// </summary>
        [JsonPropertyName("resolvesCheckIds")]
        public List<string> ResolvesCheckIds { get; set; } = new();

        /// <summary>
        /// S1: declares that this template's real-world effect cannot be confirmed at apply
        /// time (e.g. "the created maintenance jobs actually ran") and must be re-verified
        /// later. When set AND the apply outcome is AppliedVerified, the runner ledgers a
        /// RemediationVerifyScheduled entry with a verify-by deadline; the deferred-verify
        /// service re-renders the check from the REGISTERED template + the stored parameters
        /// (never from stored SQL) and resolves it pass/fail. Null = verify is complete at
        /// apply time (the default for every pre-S1 template).
        /// </summary>
        [JsonPropertyName("deferredVerify")]
        public DeferredVerifySpec? DeferredVerify { get; set; }
    }

    /// <summary>
    /// Declaration of a deferred (follow-up) verification window for a template whose
    /// effect needs time to materialise. The verify SQL is NOT stored here — it is
    /// re-rendered at verify time by <c>DeferredVerifyPlan</c> from the registered
    /// template + the apply's stored parameters, so executable SQL never round-trips
    /// through a data store.
    /// </summary>
    public sealed record DeferredVerifySpec(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("windowDays")] int WindowDays);
}
