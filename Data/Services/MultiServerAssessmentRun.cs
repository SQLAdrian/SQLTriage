/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data.Services;

/// <summary>
/// Which phase of one server's assessment a progress report describes.
///
/// <para><b>Why the phase exists (pages-r2-03).</b> The multi-server runner used to report the
/// bare tuple <c>(Completed, Total, ServerName)</c> from three places that mean three different
/// things: before a server's assessment is awaited, after it returns, and from the catch block
/// when it fails. The Vulnerability Assessment page turned every one of them into the same
/// "Assessment complete: {server}" toast. Measured live on 2026-08-28 with four targets of which
/// two do not exist: the run emitted "Assessment complete: ZZHUNTNOSUCHHOST (2/4)" for a host
/// that was never contacted, and "Assessment complete: .\OLD2017 (1/4)" before OLD2017's
/// assessment had started. A caller cannot say what happened if the payload does not carry it.</para>
/// </summary>
public enum ServerAssessmentPhase
{
    /// <summary>The server's assessment is about to be awaited. Nothing has been measured yet.</summary>
    Starting,

    /// <summary>The server's assessment returned. <c>RowCount</c> says how much it produced.</summary>
    Completed,

    /// <summary>The server's assessment threw. <c>Error</c> carries the reason.</summary>
    Failed,

    /// <summary>The run was cancelled before this server started, so it was never attempted.</summary>
    Cancelled,
}

/// <summary>
/// One progress report from <see cref="SqlAssessmentService.RunMultiServerAssessmentAsync"/>.
/// Carries what happened as well as how far the run has got, so the caller's message can be
/// conditioned on the outcome rather than on the counter alone.
/// </summary>
/// <param name="Completed">Targets finished (successfully or not) at the moment of the report.</param>
/// <param name="Total">Targets the run planned to visit.</param>
/// <param name="ServerName">The server this report is about.</param>
/// <param name="Phase">What happened to that server. See <see cref="ServerAssessmentPhase"/>.</param>
/// <param name="RowCount">Rows the server produced. Meaningful only for <c>Completed</c>.</param>
/// <param name="Error">The failure reason. Non-null only for <c>Failed</c>.</param>
public sealed record ServerAssessmentProgress(
    int Completed,
    int Total,
    string ServerName,
    ServerAssessmentPhase Phase,
    int RowCount = 0,
    string? Error = null);

/// <summary>
/// What one server's assessment actually did, recorded by the runner at the moment it happened.
/// </summary>
/// <param name="ServerName">The target, exactly as the run named it.</param>
/// <param name="RowCount">Assessment rows this server produced. Zero means it measured nothing.</param>
/// <param name="Error">Why it produced nothing, when that is known. Null on the success path.</param>
public sealed record ServerAssessmentOutcome(string ServerName, int RowCount, string? Error)
{
    /// <summary>
    /// True only when this server produced assessment rows. A server that connected and returned
    /// nothing has measured nothing, so it may not be named as assessed either.
    /// </summary>
    public bool Reported => RowCount > 0;
}

/// <summary>
/// The result of a multi-server assessment: the merged summary PLUS a per-target record of what
/// each server actually did.
///
/// <para><b>Why this replaced a bare <see cref="AssessmentSummary"/> (pages-r2-02).</b> The
/// Vulnerability Assessment page set <c>State.AssessedServers</c> from the TARGET list, which is
/// the run's intent, and printed it on the client PDF cover beside a pass rate computed only from
/// the servers that answered. The CLI sibling path already got this right
/// (<c>Cli/CliAuditHost.cs:586</c>, <c>vaState.AssessedServers = reachable;</c>) and its comment
/// names this very page as the code it mirrors. The page could not do the same because the
/// runner threw the outcome away: it logged each failure and returned only the merge.</para>
///
/// <para><b>Why the reporting set is not derivable from the merged rows.</b> Measured live on
/// 2026-08-28: after a two-live/two-dead run, <c>Summary.Results</c> held 158 rows and the
/// distinct non-empty <c>ThisServer</c> values across them numbered ZERO, because
/// <see cref="SqlAssessmentService.MergeResults"/> composes new rows and does not carry the
/// per-server identity stamp. Recovering coverage from the rows would therefore have produced a
/// cover page naming nobody. The runner records it instead.</para>
/// </summary>
public sealed class MultiServerAssessmentRun
{
    /// <summary>The merged, deduplicated summary. Exactly what the runner returned before.</summary>
    public AssessmentSummary Summary { get; init; } = new();

    /// <summary>Every target the run intended to visit, in the order it was given them.</summary>
    public IReadOnlyList<string> PlannedServers { get; init; } = new List<string>();

    /// <summary>What each target actually did.</summary>
    public IReadOnlyList<ServerAssessmentOutcome> Outcomes { get; init; } = new List<ServerAssessmentOutcome>();

    /// <summary>
    /// The servers that produced assessment rows. This is the only set a report may name as
    /// assessed, and the only set a coverage figure may be attributed to.
    /// </summary>
    public IReadOnlyList<string> ReportingServers =>
        Outcomes.Where(o => o.Reported).Select(o => o.ServerName).ToList();

    /// <summary>
    /// The targets that were attempted and produced nothing, with the reason where one is known.
    /// Named so a report can state what it does NOT cover rather than dropping it silently.
    /// </summary>
    public IReadOnlyList<ServerAssessmentOutcome> SilentServers =>
        Outcomes.Where(o => !o.Reported).ToList();
}
