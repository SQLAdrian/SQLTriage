/* In the name of God, the Merciful, the Compassionate */
/*
 * AuditFileSelection - which of one instance's audit files the app picks when the OPERATOR has not
 * picked one. Discovery can find several files for the same SQL instance (an sp_triage export and
 * an sp_Blitz export, several days of each). The Compliance Roadmap auto-selects one per instance
 * on load, and the domain "select all" toggle does the same for a whole domain.
 *
 * THE DEFECT THIS EXISTS TO FIX (2026-08-26, cold gate B-2 on the audit lane). The preference was
 * "sp_triage before sp_Blitz, then newest", read in that order, with no notion of whether the file
 * could be read at all. From 2026-08-26 the page refuses to score a file whose fired-check set is
 * unknown (AuditParseOutcome.Unreadable), because an empty fired set otherwise prints 100% and
 * maturity L5 Governed off no evidence. The two rules met and produced a new wrong answer: one
 * sp_triage export carrying no sp_Blitz section rows out-ranked a perfectly scoreable sp_Blitz
 * export for the same instance, so the page auto-selected the dead file, suppressed the whole
 * roadmap, and told the operator to re-run an audit that had in fact already run. Refusing to score
 * the dead file is right. Withholding the live report next to it is not.
 *
 * THE RULE. A file already PROVED unreadable ranks last, whatever its type or date. Everything else
 * is unchanged: sp_triage before sp_Blitz, then newest first. Rank is only ever demoted by evidence
 * (a real parse attempt that failed), never by a guess about the file's contents, so an unparsed
 * file keeps its documented place in the order.
 *
 * WHAT THIS DOES NOT DO. It never substitutes a file the operator selected by hand. When the
 * operator ticks an unreadable file, the page fails closed and says why. Substitution belongs only
 * where the APP chose the file, and the page names every file it skipped that way on screen.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data.Services;

/// <summary>
/// The three facts <see cref="AuditFileSelection"/> ranks on. Implemented by the Roadmap page's
/// own view model so the page and this rule cannot drift apart.
/// </summary>
public interface IAuditFileCandidate
{
    /// <summary>Which tool produced the file.</summary>
    AuditFileType FileType { get; }

    /// <summary>The audit date read out of the file, not the file's timestamp.</summary>
    DateTime AuditDate { get; }

    /// <summary>
    /// What a parse attempt found, or <see cref="AuditParseOutcome.NotParsed"/> when none has run.
    /// Only <see cref="AuditParseOutcome.Unreadable"/> moves a candidate, and only downwards.
    /// </summary>
    AuditParseOutcome ParseOutcome { get; }
}

/// <summary>
/// The one place that says which audit file wins when several describe the same instance. See the
/// file header for the rule and for the defect it closes.
/// </summary>
public static class AuditFileSelection
{
    /// <summary>
    /// Orders one instance's candidate files best-first: readable before proved-unreadable,
    /// sp_triage before sp_Blitz, newest audit date before older.
    /// </summary>
    public static IEnumerable<T> InPreferenceOrder<T>(IEnumerable<T> candidates)
        where T : IAuditFileCandidate
        => candidates
            .OrderBy(c => c.ParseOutcome == AuditParseOutcome.Unreadable ? 1 : 0)
            .ThenBy(c => c.FileType == AuditFileType.SpTriage ? 0 : 1)
            .ThenByDescending(c => c.AuditDate);

    /// <summary>
    /// True when this candidate has been tried and could not be read. A file in this state is still
    /// listed and still selectable by hand; it is only refused the automatic pick.
    /// </summary>
    public static bool IsProvedUnreadable(IAuditFileCandidate candidate)
        => candidate.ParseOutcome == AuditParseOutcome.Unreadable;
}
