/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>
    /// DISTINCT TERMINAL STATES of an applied remediation — never a boolean
    /// "success". A fix that did not apply must never report as applied. Maps to
    /// the canonical AuditLogService.RemediationOutcomes strings for the ledger.
    /// </summary>
    public enum RemediationOutcome
    {
        /// <summary>Applied and post-verify confirmed the change took effect.</summary>
        AppliedVerified,
        /// <summary>Applied but post-verify could not confirm — needs human review.</summary>
        AppliedVerifyFailed,
        /// <summary>Did not run (permissions / preflight). Nothing changed.</summary>
        CouldNotRun,
        /// <summary>Already compliant; no change was necessary.</summary>
        NoOp
    }

    /// <summary>
    /// Why a remediation was refused BEFORE execution (a gate blocked it). Distinct
    /// from <see cref="RemediationOutcome"/>, which describes an attempt that reached
    /// the executor.
    /// </summary>
    public enum RemediationRefusal
    {
        /// <summary>Gate 1: not a registered template / not Remediation-classified.</summary>
        NotARegisteredTemplate,
        /// <summary>Gate 2: build/licence lacks the remediation capability.</summary>
        CapabilityDenied,
        /// <summary>Gate 3: insufficient change credits.</summary>
        InsufficientCredits,
        /// <summary>Gate 4: no explicit human approval.</summary>
        NotApproved,
        /// <summary>Gate 5 pre-flight: the audit ledger is not writable.</summary>
        AuditNotWritable
    }

    public static class RemediationOutcomeMap
    {
        /// <summary>Maps a runner outcome to the canonical audit-ledger string.</summary>
        public static string ToAuditString(RemediationOutcome outcome) => outcome switch
        {
            RemediationOutcome.AppliedVerified    => AuditLogService.RemediationOutcomes.AppliedVerified,
            RemediationOutcome.AppliedVerifyFailed => AuditLogService.RemediationOutcomes.AppliedVerifyFailed,
            RemediationOutcome.CouldNotRun        => AuditLogService.RemediationOutcomes.CouldNotRun,
            RemediationOutcome.NoOp               => AuditLogService.RemediationOutcomes.NoOp,
            _                                     => AuditLogService.RemediationOutcomes.CouldNotRun
        };
    }
}
