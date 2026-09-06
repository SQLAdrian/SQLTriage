/* In the name of God, the Merciful, the Compassionate */
/*
 * Parsed corpus `remediation:` frontmatter (corpus-v2/SCHEMA.md "The remediation block" /
 * "The remediation.operation sub-block"). Feeds the app's Remediation UI three-tier contract
 * (corpus-v2/_inventory/REMEDIATION_APP_WIRING_CONTRACT.md):
 *   - Operation present + AutoFixable true  -> one-click lane (RemediationTemplateStore wraps
 *     this into a RemediationTemplate keyed by the check id).
 *   - Present, no (well-formed) Operation, or AutoFixable false -> preview-only row (show
 *     CmdletOrTemplate T-SQL, no apply button, no credit spend).
 *   - No remediation block at all -> not in the Remediation UI.
 * A malformed operation sub-block degrades THIS check to preview-only (Operation stays null,
 * a warning is recorded) — parsing never throws past SqlCheckBuilder (fail-tolerant: one bad
 * block must never zero the whole catalogue load).
 */

using System.Text.Json.Serialization;

namespace SQLTriage.Data.Models
{
    public class CheckRemediation
    {
        [JsonPropertyName("auto_fixable")]
        public bool AutoFixable { get; set; }

        [JsonPropertyName("risk_class")]
        public string? RiskClass { get; set; }

        [JsonPropertyName("method")]
        public string? Method { get; set; }

        [JsonPropertyName("cmdlet_or_template")]
        public string? CmdletOrTemplate { get; set; }

        [JsonPropertyName("reversible")]
        public bool Reversible { get; set; }

        /// <summary>
        /// Optional corpus-authored credit price for one apply. Null (the norm) leaves the
        /// app to derive it from risk_class + reversible. Out-of-range or unparseable values
        /// are dropped back to null with a warning — fail-tolerant, like the rest of this block.
        /// </summary>
        [JsonPropertyName("credit_cost")]
        public int? CreditCost { get; set; }

        [JsonPropertyName("snapshot_query")]
        public string? SnapshotQuery { get; set; }

        [JsonPropertyName("verify_query")]
        public string? VerifyQuery { get; set; }

        /// <summary>
        /// The `remediation.operation` sub-block. Null when the check carries no structured op
        /// (an honest judgment-call preview-only fix) OR when the sub-block failed to parse
        /// (malformed — logged by the caller, never thrown).
        /// </summary>
        [JsonPropertyName("operation")]
        public CheckRemediationOperation? Operation { get; set; }
    }

    /// <summary>
    /// The `remediation.operation` sub-block — mirrors the app's own
    /// <c>RemediationOperation</c>/<c>RemediationOpKind</c> shape. Only <c>sp_configure</c> and
    /// <c>db_set_option</c> have corpus entries today (<c>create_index</c> is reserved).
    /// </summary>
    public class CheckRemediationOperation
    {
        [JsonPropertyName("op_kind")]
        public string OpKind { get; set; } = string.Empty;

        // ── sp_configure fields ──
        [JsonPropertyName("config_name")]
        public string? ConfigName { get; set; }

        [JsonPropertyName("advanced_option")]
        public bool AdvancedOption { get; set; }

        [JsonPropertyName("value_param")]
        public string? ValueParam { get; set; }

        [JsonPropertyName("min_value")]
        public int? MinValue { get; set; }

        [JsonPropertyName("max_value")]
        public int? MaxValue { get; set; }

        /// <summary>Fixed target value, no operator input. Mutually exclusive with ValueParam.</summary>
        [JsonPropertyName("value_fixed")]
        public int? ValueFixed { get; set; }

        // ── db_set_option fields ──
        [JsonPropertyName("option_sql")]
        public string? OptionSql { get; set; }

        /// <summary>Read-only; returns exactly the databases needing the fix. Required for db_set_option.</summary>
        [JsonPropertyName("offenders_query")]
        public string? OffendersQuery { get; set; }
    }
}
