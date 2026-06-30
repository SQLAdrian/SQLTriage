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
        CreateIndex
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
        /// The structured change this template authorises — the SINGLE SOURCE the gate
        /// classifies and the executor runs (see <see cref="RemediationOperation"/>).
        /// Required for a <see cref="RemediationKind.Configuration"/> template: the gate
        /// renders it to T-SQL, classifies that exact text, and the executor runs the
        /// same rendering. Null only for legacy/other kinds that apply via dbatools.
        /// </summary>
        [JsonPropertyName("operation")]
        public RemediationOperation? Operation { get; set; }
    }
}
