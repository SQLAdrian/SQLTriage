/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Cli;

/// <summary>
/// Pure identity-grouping logic for <c>--report audit-evidence</c>'s duplicate-server guard —
/// deliberately extracted from <see cref="CliAuditHost.RunAuditEvidenceReportAsync"/> so the
/// decision (which requested names collide) is unit-testable without live SQL: it takes the
/// (requestedName, resolvedIdentity) pairs the CLI host already gathers during preflight
/// (<see cref="EphemeralConnectionFactory.PreflightWithIdentityAsync"/>) and does no I/O itself.
///
/// The bug this guards against: feeding the SAME instance under two requested names (e.g.
/// <c>.\new2022</c> and <c>MSI\NEW2022</c> via a servers @file) resolves both to identical
/// @@SERVERNAME, so the VA scan runs twice against one server and
/// <c>ReportBundleService.GatherAuditEvidence</c> accumulates every finding twice into one
/// attestation PDF — a compliance artifact silently claiming N servers were assessed when 1 was.
///
/// CHOICE: REFUSE, not dedupe-with-warning. A compliance attestation is meant to be handed to an
/// auditor as evidence; silently keeping one alias and discarding the other still ships a
/// document whose declared server count the operator did not consciously choose, and a warning
/// buried in stdout/stderr is easy to miss in an unattended (Task Scheduler) run — the artifact
/// itself carries no trace of which alias survived. Refusing forces the operator to fix the
/// server list (or accept the collision knowingly by removing the duplicate themselves) and
/// costs nothing: the run has connected but not yet scanned or built anything, so refusing here
/// is still "before any run" in the same sense CliAuditHost's exit-code-3 class already means.
/// </summary>
public static class AuditEvidenceIdentityGrouping
{
    /// <summary>One resolved server identity that two or more requested names collapsed onto.</summary>
    public sealed record DuplicateIdentityGroup(string ResolvedIdentity, IReadOnlyList<string> RequestedNames);

    /// <summary>
    /// Groups requested names by resolved identity and returns every group with more than one
    /// requested name in it (aliases of the same server, or the same name typed twice — both
    /// produce the duplicated-content bug and both are reported).
    ///
    /// Comparison is case-insensitive ordinal: SQL Server names are not a case-sensitive
    /// discriminator (<c>@@SERVERNAME</c> reflects the server's own registered case, but
    /// <c>NEW2022\INSTANCE</c> and <c>new2022\instance</c> name the same instance, not two).
    /// Entries whose <c>ResolvedIdentity</c> is null or empty (preflight could not read
    /// @@SERVERNAME — see <see cref="EphemeralConnectionFactory.PreflightWithIdentityAsync"/>'s
    /// class doc) are never grouped with anything: an unknown identity must not manufacture a
    /// false duplicate, or suppress a real one, against another unknown identity.
    /// </summary>
    public static IReadOnlyList<DuplicateIdentityGroup> FindDuplicates(
        IReadOnlyList<(string RequestedName, string? ResolvedIdentity)> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return resolved
            .Where(p => !string.IsNullOrWhiteSpace(p.ResolvedIdentity))
            .GroupBy(p => p.ResolvedIdentity!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateIdentityGroup(g.Key, g.Select(p => p.RequestedName).ToList()))
            .ToList();
    }
}
